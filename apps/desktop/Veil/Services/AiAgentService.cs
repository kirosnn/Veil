using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Veil.Configuration;

namespace Veil.Services;

internal sealed class AiAgentService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly AiSecretStore _secretStore = new();

    internal string ProviderDisplayName => AiProviderKind.ToDisplayName(_settings.AiProvider);

    internal async Task<string> GenerateReplyAsync(IReadOnlyList<AiAgentTurn> conversation, CancellationToken cancellationToken)
    {
        if (conversation.Count == 0)
        {
            throw new InvalidOperationException("The conversation is empty.");
        }

        return _settings.AiProvider switch
        {
            AiProviderKind.ChatGptPremium => await SendChatGptPremiumAsync(conversation, cancellationToken),
            AiProviderKind.OpenAi => await SendOpenAiCompatibleAsync(
                _settings.OpenAiBaseUrl,
                _settings.OpenAiModel,
                RequireSecret(AiSecretNames.OpenAiApiKey, "OpenAI API key"),
                conversation,
                cancellationToken),
            AiProviderKind.Mistral => await SendOpenAiCompatibleAsync(
                _settings.MistralBaseUrl,
                _settings.MistralModel,
                RequireSecret(AiSecretNames.MistralApiKey, "Mistral API key"),
                conversation,
                cancellationToken),
            AiProviderKind.Anthropic => await SendAnthropicAsync(
                _settings.AnthropicBaseUrl,
                _settings.AnthropicModel,
                RequireSecret(AiSecretNames.AnthropicApiKey, "Anthropic API key"),
                conversation,
                cancellationToken),
            AiProviderKind.Ollama => await SendOllamaAsync(
                _settings.OllamaBaseUrl,
                _settings.OllamaModel,
                null,
                conversation,
                cancellationToken),
            AiProviderKind.OllamaCloud => await SendOllamaAsync(
                _settings.OllamaCloudBaseUrl,
                _settings.OllamaCloudModel,
                RequireSecret(AiSecretNames.OllamaCloudApiKey, "Ollama Cloud API key"),
                conversation,
                cancellationToken),
            _ => throw new InvalidOperationException("The selected AI provider is not supported.")
        };
    }

    private async Task<string> SendChatGptPremiumAsync(
        IReadOnlyList<AiAgentTurn> conversation,
        CancellationToken cancellationToken)
    {
        string authPayload = LoadChatGptAuthPayload();
        string tempRoot = Path.Combine(Path.GetTempPath(), "Veil", "finder-ai", Guid.NewGuid().ToString("N"));
        string codexHome = Path.Combine(tempRoot, ".codex");
        string authPath = Path.Combine(codexHome, "auth.json");
        string outputPath = Path.Combine(tempRoot, "last-message.txt");

        Directory.CreateDirectory(codexHome);
        await File.WriteAllTextAsync(authPath, authPayload, cancellationToken);

        string executablePath = GetCodexExecutablePath();
        string prompt = BuildCodexPrompt(conversation);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--skip-git-repo-check");
        startInfo.ArgumentList.Add("--ephemeral");
        startInfo.ArgumentList.Add("--sandbox");
        startInfo.ArgumentList.Add("read-only");
        startInfo.ArgumentList.Add("--color");
        startInfo.ArgumentList.Add("never");
        startInfo.ArgumentList.Add("--cd");
        startInfo.ArgumentList.Add(ResolveWorkspaceRoot());
        startInfo.ArgumentList.Add("--output-last-message");
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(RequireModel(_settings.ChatGptModel));
        startInfo.ArgumentList.Add("-");

        startInfo.Environment["HOME"] = tempRoot;
        startInfo.Environment["USERPROFILE"] = tempRoot;
        startInfo.Environment["CODEX_HOME"] = codexHome;

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        try
        {
            if (process.ExitCode == 0 && File.Exists(outputPath))
            {
                string result = (await File.ReadAllTextAsync(outputPath, cancellationToken)).Trim();
                if (result.Length > 0)
                {
                    return result;
                }
            }

            string error = BuildProcessError(stderr, stdout);
            throw new InvalidOperationException(error);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private async Task<string> SendOpenAiCompatibleAsync(
        string baseUrl,
        string model,
        string apiKey,
        IReadOnlyList<AiAgentTurn> conversation,
        CancellationToken cancellationToken)
    {
        string normalizedModel = RequireModel(model);
        Uri endpoint = BuildEndpoint(baseUrl, "chat", "completions");
        object[] messages = BuildOpenAiCompatibleMessages(conversation);
        var payload = new
        {
            model = normalizedModel,
            messages
        };

        using HttpRequestMessage request = CreateJsonRequest(HttpMethod.Post, endpoint, payload);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
        string json = await ReadResponseBodyAsync(response, cancellationToken);
        using JsonDocument document = ParseResponse(json);

        JsonElement root = document.RootElement;
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(GetErrorMessage(root, response.ReasonPhrase));
        }

        if (root.TryGetProperty("choices", out JsonElement choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out JsonElement message) &&
            message.TryGetProperty("content", out JsonElement content))
        {
            string text = ExtractTextContent(content);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        throw new InvalidOperationException("The provider returned an empty response.");
    }

    private async Task<string> SendAnthropicAsync(
        string baseUrl,
        string model,
        string apiKey,
        IReadOnlyList<AiAgentTurn> conversation,
        CancellationToken cancellationToken)
    {
        string normalizedModel = RequireModel(model);
        Uri endpoint = BuildEndpoint(baseUrl, "v1", "messages");
        object[] messages = conversation
            .Select(static turn => new
            {
                role = turn.Role,
                content = turn.Content
            })
            .ToArray();
        var payload = new
        {
            model = normalizedModel,
            max_tokens = 1024,
            system = BuildSystemPrompt(),
            messages
        };

        using HttpRequestMessage request = CreateJsonRequest(HttpMethod.Post, endpoint, payload);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
        string json = await ReadResponseBodyAsync(response, cancellationToken);
        using JsonDocument document = ParseResponse(json);

        JsonElement root = document.RootElement;
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(GetErrorMessage(root, response.ReasonPhrase));
        }

        if (root.TryGetProperty("content", out JsonElement content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (JsonElement item in content.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out JsonElement typeElement) ||
                    !string.Equals(typeElement.GetString(), "text", StringComparison.OrdinalIgnoreCase) ||
                    !item.TryGetProperty("text", out JsonElement textElement))
                {
                    continue;
                }

                string? part = textElement.GetString();
                if (!string.IsNullOrWhiteSpace(part))
                {
                    if (builder.Length > 0)
                    {
                        builder.AppendLine();
                        builder.AppendLine();
                    }

                    builder.Append(part.Trim());
                }
            }

            if (builder.Length > 0)
            {
                return builder.ToString();
            }
        }

        throw new InvalidOperationException("The provider returned an empty response.");
    }

    private async Task<string> SendOllamaAsync(
        string baseUrl,
        string model,
        string? apiKey,
        IReadOnlyList<AiAgentTurn> conversation,
        CancellationToken cancellationToken)
    {
        string normalizedModel = RequireModel(model);
        Uri endpoint = BuildEndpoint(baseUrl, "chat");
        object[] messages = BuildOpenAiCompatibleMessages(conversation);
        var payload = new
        {
            model = normalizedModel,
            stream = false,
            messages
        };

        using HttpRequestMessage request = CreateJsonRequest(HttpMethod.Post, endpoint, payload);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
        string json = await ReadResponseBodyAsync(response, cancellationToken);
        using JsonDocument document = ParseResponse(json);

        JsonElement root = document.RootElement;
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(GetErrorMessage(root, response.ReasonPhrase));
        }

        if (root.TryGetProperty("message", out JsonElement message) &&
            message.TryGetProperty("content", out JsonElement content))
        {
            string text = ExtractTextContent(content);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        throw new InvalidOperationException("The provider returned an empty response.");
    }

    private static object[] BuildOpenAiCompatibleMessages(IReadOnlyList<AiAgentTurn> conversation)
    {
        var messages = new object[conversation.Count + 1];
        messages[0] = new
        {
            role = "system",
            content = BuildSystemPrompt()
        };

        for (int index = 0; index < conversation.Count; index++)
        {
            AiAgentTurn turn = conversation[index];
            messages[index + 1] = new
            {
                role = turn.Role,
                content = turn.Content
            };
        }

        return messages;
    }

    private static HttpRequestMessage CreateJsonRequest(HttpMethod method, Uri endpoint, object payload)
    {
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        return new HttpRequestMessage(method, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string BuildSystemPrompt()
    {
        return "You are Veil, a native desktop AI assistant opened from Finder. Give concise, practical answers. When the request is about local code, reason carefully and prefer concrete guidance.";
    }

    private static string BuildCodexPrompt(IReadOnlyList<AiAgentTurn> conversation)
    {
        var builder = new StringBuilder();
        builder.AppendLine(BuildSystemPrompt());
        builder.AppendLine();
        builder.AppendLine("Stay in assistant mode. Do not modify files. You may inspect the current workspace read-only if it helps.");
        builder.AppendLine();
        builder.AppendLine("Conversation:");

        foreach (AiAgentTurn turn in conversation)
        {
            builder.AppendLine();
            builder.Append(turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "Assistant" : "User");
            builder.AppendLine(":");
            builder.AppendLine(turn.Content.Trim());
        }

        builder.AppendLine();
        builder.AppendLine("Reply to the latest user message.");
        return builder.ToString();
    }

    private static string RequireModel(string value)
    {
        string normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("No model is configured for the selected AI provider.");
        }

        return normalized;
    }

    private string RequireSecret(string secretName, string label)
    {
        string? secret = _secretStore.LoadSecret(secretName);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException($"{label} is missing in Settings.");
        }

        return secret.Trim();
    }

    private string LoadChatGptAuthPayload()
    {
        string? payload = _secretStore.LoadSecret(AiSecretNames.ChatGptOAuthPayload);
        if (!string.IsNullOrWhiteSpace(payload))
        {
            return payload;
        }

        string candidatePath = _settings.ChatGptAuthFilePath.Trim();
        if (candidatePath.Length == 0)
        {
            candidatePath = AiSecretStore.DetectDefaultChatGptAuthPath() ?? string.Empty;
        }

        if (candidatePath.Length == 0 || !File.Exists(candidatePath))
        {
            throw new InvalidOperationException("No ChatGPT auth payload is stored. Import your local auth file in Settings.");
        }

        return File.ReadAllText(candidatePath);
    }

    private static string GetCodexExecutablePath()
    {
        string executablePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm",
            "codex.cmd");

        return File.Exists(executablePath) ? executablePath : "codex";
    }

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            bool hasGit = Directory.Exists(Path.Combine(current.FullName, ".git"));
            bool hasSolution = File.Exists(Path.Combine(current.FullName, "Veil.sln"));
            if (hasGit || hasSolution)
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Environment.CurrentDirectory;
    }

    private static string BuildProcessError(string stderr, string stdout)
    {
        string preferred = stderr.Trim();
        if (preferred.Length == 0)
        {
            preferred = stdout.Trim();
        }

        if (preferred.Length == 0)
        {
            return "The OpenAI OAuth request failed.";
        }

        string[] lines = preferred
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "The OpenAI OAuth request failed." : lines[^1];
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static Uri BuildEndpoint(string baseUrl, params string[] segments)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidOperationException("The configured provider URL is not a valid absolute URL.");
        }

        var builder = new UriBuilder(uri);
        string path = builder.Path.TrimEnd('/');
        foreach (string rawSegment in segments)
        {
            string segment = rawSegment.Trim('/');
            if (segment.Length == 0)
            {
                continue;
            }

            if (path.EndsWith('/' + segment, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path.Trim('/'), segment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            path = $"{path}/{segment}";
        }

        builder.Path = path.Length == 0 ? "/" : path;
        return builder.Uri;
    }

    private static string ExtractTextContent(JsonElement content)
    {
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join(
                Environment.NewLine + Environment.NewLine,
                content.EnumerateArray()
                    .Select(ExtractTextPart)
                    .Where(static value => !string.IsNullOrWhiteSpace(value))),
            JsonValueKind.Object => ExtractTextPart(content),
            _ => string.Empty
        };
    }

    private static string ExtractTextPart(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            return item.GetString() ?? string.Empty;
        }

        if (item.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (item.TryGetProperty("text", out JsonElement textElement) && textElement.ValueKind == JsonValueKind.String)
        {
            return textElement.GetString() ?? string.Empty;
        }

        if (item.TryGetProperty("content", out JsonElement contentElement))
        {
            return ExtractTextContent(contentElement);
        }

        return string.Empty;
    }

    private static async Task<string> ReadResponseBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return string.Empty;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static JsonDocument ParseResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return JsonDocument.Parse("{}");
        }

        return JsonDocument.Parse(json);
    }

    private static string GetErrorMessage(JsonElement root, string? fallbackReason)
    {
        if (root.TryGetProperty("error", out JsonElement error))
        {
            if (error.ValueKind == JsonValueKind.String)
            {
                string? text = error.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }

            if (error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("message", out JsonElement message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    string? text = message.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text.Trim();
                    }
                }

                if (error.TryGetProperty("type", out JsonElement type) &&
                    type.ValueKind == JsonValueKind.String)
                {
                    string? text = type.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text.Trim();
                    }
                }
            }
        }

        return string.IsNullOrWhiteSpace(fallbackReason)
            ? "The provider request failed."
            : fallbackReason.Trim();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(90)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VeilDesktop/1.0");
        return client;
    }
}

internal sealed record AiAgentTurn(string Role, string Content);
