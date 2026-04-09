using System.Text.Json;
using Veil.Configuration;
using Veil.Diagnostics;

namespace Veil.Services;

internal sealed class AiModelCatalogService
{
    private const string CatalogUrl = "https://models.dev/api.json";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly string _cachePath;

    internal AiModelCatalogService()
    {
        _cachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Veil",
            "cache",
            "models.dev.json");
    }

    internal async Task<AiModelCatalogSnapshot> GetModelsForProviderAsync(
        string providerKind,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        string? json = null;
        string sourceLabel = "Built-in catalog";

        if (!forceRefresh)
        {
            json = await TryReadFreshCacheAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(json))
            {
                sourceLabel = "models.dev cache";
            }
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            try
            {
                json = await HttpClient.GetStringAsync(CatalogUrl, cancellationToken);
                await WriteCacheAsync(json, cancellationToken);
                sourceLabel = "models.dev";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
            {
                AppLogger.Error("Failed to refresh models.dev catalog.", ex);
                json = await TryReadAnyCacheAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    sourceLabel = "models.dev cache";
                }
            }
        }

        IReadOnlyList<AiModelCatalogEntry> models = string.IsNullOrWhiteSpace(json)
            ? GetFallbackModels(providerKind)
            : ParseProviderModels(json, providerKind);

        if (models.Count == 0)
        {
            models = GetFallbackModels(providerKind);
            sourceLabel = "Built-in catalog";
        }

        string providerDisplayName = AiProviderKind.ToDisplayName(providerKind);
        string statusText = $"{models.Count} models available for {providerDisplayName} via {sourceLabel}.";
        return new AiModelCatalogSnapshot(models, statusText, sourceLabel);
    }

    private async Task<string?> TryReadFreshCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath))
        {
            return null;
        }

        DateTime lastWriteUtc = File.GetLastWriteTimeUtc(_cachePath);
        if ((DateTime.UtcNow - lastWriteUtc) > CacheLifetime)
        {
            return null;
        }

        return await File.ReadAllTextAsync(_cachePath, cancellationToken);
    }

    private async Task<string?> TryReadAnyCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(_cachePath, cancellationToken);
    }

    private async Task WriteCacheAsync(string json, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        await File.WriteAllTextAsync(_cachePath, json, cancellationToken);
    }

    private static IReadOnlyList<AiModelCatalogEntry> ParseProviderModels(string json, string providerKind)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        var results = new List<AiModelCatalogEntry>();

        foreach ((string providerId, JsonElement providerElement) in EnumerateProviders(document.RootElement))
        {
            if (!MatchesProvider(providerKind, providerId, providerElement))
            {
                continue;
            }

            string providerName = TryGetString(providerElement, "name") ?? providerId;
            ParseProviderModels(providerElement, providerId, providerName, results);
        }

        return results
            .GroupBy(static item => item.ModelId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static item => item.DisplayLabel, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<(string ProviderId, JsonElement ProviderElement)> EnumerateProviders(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (root.TryGetProperty("providers", out JsonElement providersElement))
        {
            foreach ((string providerId, JsonElement providerElement) in EnumerateProviderContainer(providersElement))
            {
                yield return (providerId, providerElement);
            }

            yield break;
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            yield return (property.Name, property.Value);
        }
    }

    private static IEnumerable<(string ProviderId, JsonElement ProviderElement)> EnumerateProviderContainer(JsonElement providersElement)
    {
        if (providersElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in providersElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    yield return (property.Name, property.Value);
                }
            }

            yield break;
        }

        if (providersElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement item in providersElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? providerId = TryGetString(item, "id") ?? TryGetString(item, "provider");
            if (!string.IsNullOrWhiteSpace(providerId))
            {
                yield return (providerId, item);
            }
        }
    }

    private static bool MatchesProvider(string providerKind, string providerId, JsonElement providerElement)
    {
        string normalizedProviderId = providerId.Trim().ToLowerInvariant();
        string providerName = (TryGetString(providerElement, "name") ?? providerId).Trim().ToLowerInvariant();

        foreach (string alias in GetProviderAliases(providerKind))
        {
            if (normalizedProviderId == alias || providerName.Contains(alias, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] GetProviderAliases(string providerKind)
    {
        return providerKind switch
        {
            AiProviderKind.ChatGptPremium => ["openai"],
            AiProviderKind.OpenAi => ["openai"],
            AiProviderKind.Anthropic => ["anthropic"],
            AiProviderKind.Mistral => ["mistral"],
            AiProviderKind.Ollama => ["ollama"],
            AiProviderKind.OllamaCloud => ["ollama"],
            _ => []
        };
    }

    private static void ParseProviderModels(
        JsonElement providerElement,
        string providerId,
        string providerName,
        ICollection<AiModelCatalogEntry> results)
    {
        if (!providerElement.TryGetProperty("models", out JsonElement modelsElement))
        {
            return;
        }

        if (modelsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in modelsElement.EnumerateObject())
            {
                if (TryBuildEntry(property.Value, providerId, providerName, property.Name, out AiModelCatalogEntry? entry))
                {
                    results.Add(entry);
                }
            }

            return;
        }

        if (modelsElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement item in modelsElement.EnumerateArray())
        {
            if (TryBuildEntry(item, providerId, providerName, null, out AiModelCatalogEntry? entry))
            {
                results.Add(entry);
            }
        }
    }

    private static bool TryBuildEntry(
        JsonElement element,
        string providerId,
        string providerName,
        string? fallbackId,
        out AiModelCatalogEntry entry)
    {
        entry = null!;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? modelId = TryGetString(element, "id")
            ?? TryGetString(element, "model")
            ?? TryGetString(element, "model_id")
            ?? fallbackId;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        string displayName = TryGetString(element, "name")
            ?? TryGetString(element, "label")
            ?? modelId;
        string? summary = BuildSummary(element);
        entry = new AiModelCatalogEntry(
            providerId,
            providerName,
            modelId.Trim(),
            displayName.Trim(),
            BuildDisplayLabel(modelId, displayName, summary),
            summary);
        return true;
    }

    private static string BuildDisplayLabel(string modelId, string displayName, string? summary)
    {
        string label = string.Equals(modelId, displayName, StringComparison.OrdinalIgnoreCase)
            ? modelId.Trim()
            : $"{displayName.Trim()} ({modelId.Trim()})";

        return string.IsNullOrWhiteSpace(summary)
            ? label
            : $"{label}  •  {summary}";
    }

    private static string? BuildSummary(JsonElement element)
    {
        string? contextLimit = TryGetNestedNumberString(element, "limit", "context");
        string? releaseDate = TryGetString(element, "release_date");
        string? reasoning = TryGetBooleanString(element, "reasoning", "reasoning");

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(contextLimit))
        {
            parts.Add($"{contextLimit} ctx");
        }

        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            parts.Add(reasoning);
        }

        if (!string.IsNullOrWhiteSpace(releaseDate))
        {
            parts.Add(releaseDate);
        }

        return parts.Count == 0 ? null : string.Join("  •  ", parts);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? TryGetNestedNumberString(JsonElement element, string propertyName, string nestedPropertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(nestedPropertyName, out JsonElement nested))
        {
            return null;
        }

        return nested.ValueKind switch
        {
            JsonValueKind.Number when nested.TryGetInt64(out long numberValue) => numberValue.ToString("N0"),
            JsonValueKind.String => nested.GetString(),
            _ => null
        };
    }

    private static string? TryGetBooleanString(JsonElement element, string propertyName, string label)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.True)
        {
            return null;
        }

        return label;
    }

    private static IReadOnlyList<AiModelCatalogEntry> GetFallbackModels(string providerKind)
    {
        return GetFallbackModelDefinitions(providerKind)
            .Select(modelId => new AiModelCatalogEntry(
                providerKind,
                AiProviderKind.ToDisplayName(providerKind),
                modelId,
                modelId,
                modelId,
                null))
            .ToArray();
    }

    private static string[] GetFallbackModelDefinitions(string providerKind)
    {
        return providerKind switch
        {
            AiProviderKind.ChatGptPremium => ["gpt-5.4", "gpt-5", "gpt-4.1"],
            AiProviderKind.OpenAi => ["gpt-5.4", "gpt-5", "gpt-4.1"],
            AiProviderKind.Anthropic => ["claude-sonnet-4-20250514", "claude-opus-4-20250514", "claude-3-7-sonnet-latest"],
            AiProviderKind.Mistral => ["mistral-large-latest", "ministral-8b-latest", "pixtral-large-latest"],
            AiProviderKind.Ollama => ["qwen3-coder", "gpt-oss:120b", "llama3.3"],
            AiProviderKind.OllamaCloud => ["gpt-oss:120b", "qwen3-coder", "llama3.3"],
            _ => []
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VeilDesktop/1.0");
        return client;
    }
}

internal sealed record AiModelCatalogEntry(
    string ProviderId,
    string ProviderName,
    string ModelId,
    string DisplayName,
    string DisplayLabel,
    string? Summary);

internal sealed record AiModelCatalogSnapshot(
    IReadOnlyList<AiModelCatalogEntry> Models,
    string StatusText,
    string SourceLabel);
