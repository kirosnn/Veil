using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Veil.Services;

internal sealed class AiSecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Veil.AI.SecretStore.v1");
    private readonly string _secretDirectoryPath;

    internal AiSecretStore()
    {
        _secretDirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Veil",
            "secrets",
            "ai");
    }

    internal bool HasSecret(string secretName)
    {
        return File.Exists(GetSecretPath(secretName));
    }

    internal void SaveSecret(string secretName, string secretValue)
    {
        if (string.IsNullOrWhiteSpace(secretValue))
        {
            throw new ArgumentException("Secret value cannot be empty.", nameof(secretValue));
        }

        Directory.CreateDirectory(_secretDirectoryPath);
        byte[] payload = Encoding.UTF8.GetBytes(secretValue.Trim());
        byte[] encryptedPayload = ProtectedData.Protect(payload, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(GetSecretPath(secretName), encryptedPayload);
        Array.Clear(payload, 0, payload.Length);
    }

    internal string? LoadSecret(string secretName)
    {
        string secretPath = GetSecretPath(secretName);
        if (!File.Exists(secretPath))
        {
            return null;
        }

        byte[] encryptedPayload = File.ReadAllBytes(secretPath);
        byte[] payload = ProtectedData.Unprotect(encryptedPayload, Entropy, DataProtectionScope.CurrentUser);

        try
        {
            return Encoding.UTF8.GetString(payload);
        }
        finally
        {
            Array.Clear(payload, 0, payload.Length);
        }
    }

    internal void DeleteSecret(string secretName)
    {
        string secretPath = GetSecretPath(secretName);
        if (File.Exists(secretPath))
        {
            File.Delete(secretPath);
        }
    }

    internal bool TryImportChatGptAuth(string authFilePath, out string errorMessage)
    {
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(authFilePath))
        {
            errorMessage = "Auth file path is required.";
            return false;
        }

        string normalizedPath = authFilePath.Trim();
        if (!File.Exists(normalizedPath))
        {
            errorMessage = "Auth file was not found.";
            return false;
        }

        string rawJson;
        try
        {
            rawJson = File.ReadAllText(normalizedPath);
        }
        catch (Exception ex)
        {
            errorMessage = $"Unable to read auth file: {ex.Message}";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(rawJson);
            if (!document.RootElement.TryGetProperty("tokens", out JsonElement tokens))
            {
                errorMessage = "Auth file does not contain a tokens object.";
                return false;
            }

            if (!tokens.TryGetProperty("access_token", out _) || !tokens.TryGetProperty("refresh_token", out _))
            {
                errorMessage = "Auth file does not contain the expected OAuth tokens.";
                return false;
            }

            SaveSecret(AiSecretNames.ChatGptOAuthPayload, rawJson);
            return true;
        }
        catch (JsonException)
        {
            errorMessage = "Auth file is not valid JSON.";
            return false;
        }
    }

    internal static string? DetectDefaultChatGptAuthPath()
    {
        foreach (string candidatePath in GetDefaultChatGptAuthPaths())
        {
            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> GetDefaultChatGptAuthPaths()
    {
        string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            Path.Combine(userProfilePath, ".codex", "auth.json"),
            Path.Combine(userProfilePath, ".chatgpt-local", "auth.json")
        ];
    }

    private string GetSecretPath(string secretName)
    {
        string safeFileName = string.Concat(secretName.Select(static c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(_secretDirectoryPath, safeFileName + ".bin");
    }
}

internal static class AiSecretNames
{
    public const string ChatGptOAuthPayload = "chatgpt-oauth-payload";
    public const string OpenAiApiKey = "openai-api-key";
    public const string AnthropicApiKey = "anthropic-api-key";
    public const string MistralApiKey = "mistral-api-key";
    public const string OllamaCloudApiKey = "ollama-cloud-api-key";
}
