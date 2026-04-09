using System.Text.Json;
using Veil.Configuration;

namespace Veil.Services;

internal static class AiProviderValidationService
{
    internal static AiProviderValidationResult Validate(AppSettings settings, AiSecretStore secretStore)
    {
        return settings.AiProvider switch
        {
            AiProviderKind.ChatGptPremium => ValidateChatGpt(settings, secretStore),
            AiProviderKind.OpenAi => ValidateRemoteApiProvider(
                settings.OpenAiBaseUrl,
                settings.OpenAiModel,
                secretStore.HasSecret(AiSecretNames.OpenAiApiKey),
                "OpenAI API key"),
            AiProviderKind.Anthropic => ValidateRemoteApiProvider(
                settings.AnthropicBaseUrl,
                settings.AnthropicModel,
                secretStore.HasSecret(AiSecretNames.AnthropicApiKey),
                "Anthropic API key"),
            AiProviderKind.Mistral => ValidateRemoteApiProvider(
                settings.MistralBaseUrl,
                settings.MistralModel,
                secretStore.HasSecret(AiSecretNames.MistralApiKey),
                "Mistral API key"),
            AiProviderKind.Ollama => ValidateOllama(settings.OllamaBaseUrl, settings.OllamaModel),
            AiProviderKind.OllamaCloud => ValidateRemoteApiProvider(
                settings.OllamaCloudBaseUrl,
                settings.OllamaCloudModel,
                secretStore.HasSecret(AiSecretNames.OllamaCloudApiKey),
                "Ollama Cloud API key"),
            _ => new AiProviderValidationResult(
                AiProviderValidationState.Invalid,
                "Unknown AI provider.",
                [new AiProviderValidationMessage(false, "The selected provider is not recognized.")])
        };
    }

    private static AiProviderValidationResult ValidateChatGpt(AppSettings settings, AiSecretStore secretStore)
    {
        var messages = new List<AiProviderValidationMessage>();
        bool hasImportedAuth = secretStore.HasSecret(AiSecretNames.ChatGptOAuthPayload);
        string authPath = string.IsNullOrWhiteSpace(settings.ChatGptAuthFilePath)
            ? AiSecretStore.DetectDefaultChatGptAuthPath() ?? string.Empty
            : settings.ChatGptAuthFilePath.Trim();
        bool hasCodex = TryLocateCodexExecutable(out _);
        bool hasUsableAuth = false;

        if (string.IsNullOrWhiteSpace(settings.ChatGptModel))
        {
            messages.Add(new AiProviderValidationMessage(false, "Choose a model for OpenAI OAuth."));
        }
        else
        {
            messages.Add(new AiProviderValidationMessage(true, $"Model ready: {settings.ChatGptModel}."));
        }

        messages.Add(hasCodex
            ? new AiProviderValidationMessage(true, "Codex bridge was found on this machine.")
            : new AiProviderValidationMessage(false, "Codex CLI is missing, so OpenAI OAuth cannot be launched from Veil."));

        if (hasImportedAuth)
        {
            hasUsableAuth = true;
            messages.Add(new AiProviderValidationMessage(true, "Encrypted local OAuth payload is stored for the current Windows account."));
        }

        if (string.IsNullOrWhiteSpace(authPath))
        {
            if (!hasImportedAuth)
            {
                messages.Add(new AiProviderValidationMessage(false, "No local Codex or ChatGPT auth file was detected on this machine."));
            }
        }
        else if (!File.Exists(authPath))
        {
            if (!hasImportedAuth)
            {
                messages.Add(new AiProviderValidationMessage(false, "The configured local auth file does not exist."));
            }
        }
        else if (!TryValidateChatGptAuthFile(authPath, out string authMessage))
        {
            if (!hasImportedAuth)
            {
                messages.Add(new AiProviderValidationMessage(false, authMessage));
            }
        }
        else
        {
            hasUsableAuth = true;
            messages.Add(new AiProviderValidationMessage(true, hasImportedAuth
                ? "Local auth file is also available as a fallback source."
                : "Local auth file looks valid and can be used directly by Veil."));
        }

        if (!hasImportedAuth && hasUsableAuth)
        {
            messages.Add(new AiProviderValidationMessage(true, "Importing the auth file is optional, but keeps an encrypted local copy inside Veil."));
        }

        return BuildResult(messages, hasCodex && hasUsableAuth
            ? "OpenAI OAuth is ready on this machine."
            : "OpenAI OAuth still needs a valid local bridge or auth source.");
    }

    private static AiProviderValidationResult ValidateRemoteApiProvider(string baseUrl, string model, bool hasSecret, string secretLabel)
    {
        var messages = new List<AiProviderValidationMessage>();

        if (TryValidateHttpsUrl(baseUrl, out string urlMessage))
        {
            messages.Add(new AiProviderValidationMessage(true, urlMessage));
        }
        else
        {
            messages.Add(new AiProviderValidationMessage(false, urlMessage));
        }

        messages.Add(string.IsNullOrWhiteSpace(model)
            ? new AiProviderValidationMessage(false, "Choose a default model.")
            : new AiProviderValidationMessage(true, $"Model ready: {model.Trim()}."));

        messages.Add(hasSecret
            ? new AiProviderValidationMessage(true, $"{secretLabel} is stored locally in encrypted form.")
            : new AiProviderValidationMessage(false, $"{secretLabel} is missing."));

        return BuildResult(messages, hasSecret
            ? "Provider configuration looks complete."
            : "Provider configuration is incomplete.");
    }

    private static AiProviderValidationResult ValidateOllama(string baseUrl, string model)
    {
        var messages = new List<AiProviderValidationMessage>();

        if (TryValidateLocalOllamaUrl(baseUrl, out string urlMessage))
        {
            messages.Add(new AiProviderValidationMessage(true, urlMessage));
        }
        else
        {
            messages.Add(new AiProviderValidationMessage(false, urlMessage));
        }

        messages.Add(string.IsNullOrWhiteSpace(model)
            ? new AiProviderValidationMessage(false, "Choose a local Ollama model.")
            : new AiProviderValidationMessage(true, $"Model ready: {model.Trim()}."));

        return BuildResult(messages, "Ollama should stay on a local endpoint.");
    }

    private static bool TryValidateChatGptAuthFile(string authFilePath, out string message)
    {
        message = "The local auth file looks valid.";

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(authFilePath));
            if (!document.RootElement.TryGetProperty("tokens", out JsonElement tokens))
            {
                message = "The auth file does not contain a tokens object.";
                return false;
            }

            if (!tokens.TryGetProperty("access_token", out _) || !tokens.TryGetProperty("refresh_token", out _))
            {
                message = "The auth file does not contain the expected OAuth tokens.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            message = $"The auth file could not be validated: {ex.Message}";
            return false;
        }
    }

    private static bool TryValidateHttpsUrl(string baseUrl, out string message)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out Uri? uri))
        {
            message = "Base URL is not a valid absolute URL.";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            message = "Base URL must use HTTPS.";
            return false;
        }

        message = $"Endpoint looks valid: {uri.Host}.";
        return true;
    }

    private static bool TryValidateLocalOllamaUrl(string baseUrl, out string message)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out Uri? uri))
        {
            message = "Ollama endpoint is not a valid absolute URL.";
            return false;
        }

        bool isLoopback = uri.IsLoopback
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        if (!isLoopback)
        {
            message = "Ollama should point to a loopback or localhost endpoint only.";
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            message = "Ollama endpoint must use HTTP or HTTPS.";
            return false;
        }

        message = $"Local endpoint looks valid: {uri.Host}:{uri.Port}.";
        return true;
    }

    private static bool TryLocateCodexExecutable(out string path)
    {
        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string candidate = Path.Combine(appDataPath, "npm", "codex.cmd");
        if (File.Exists(candidate))
        {
            path = candidate;
            return true;
        }

        string? rawPath = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(rawPath))
        {
            foreach (string segment in rawPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    candidate = Path.Combine(segment, "codex.cmd");
                    if (File.Exists(candidate))
                    {
                        path = candidate;
                        return true;
                    }

                    candidate = Path.Combine(segment, "codex.exe");
                    if (File.Exists(candidate))
                    {
                        path = candidate;
                        return true;
                    }
                }
                catch
                {
                }
            }
        }

        path = string.Empty;
        return false;
    }

    private static AiProviderValidationResult BuildResult(
        IReadOnlyList<AiProviderValidationMessage> messages,
        string summary)
    {
        bool hasInvalid = messages.Any(message => !message.IsValid);
        bool hasValid = messages.Any(message => message.IsValid);

        AiProviderValidationState state = hasInvalid
            ? hasValid ? AiProviderValidationState.Warning : AiProviderValidationState.Invalid
            : AiProviderValidationState.Valid;

        return new AiProviderValidationResult(state, summary, messages);
    }
}

internal sealed record AiProviderValidationResult(
    AiProviderValidationState State,
    string Summary,
    IReadOnlyList<AiProviderValidationMessage> Messages);

internal sealed record AiProviderValidationMessage(bool IsValid, string Text);

internal enum AiProviderValidationState
{
    Valid,
    Warning,
    Invalid
}
