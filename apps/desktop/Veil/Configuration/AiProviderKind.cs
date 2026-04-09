namespace Veil.Configuration;

internal static class AiProviderKind
{
    public const string ChatGptPremium = "ChatGptPremium";
    public const string OpenAi = "OpenAI";
    public const string Anthropic = "Anthropic";
    public const string Mistral = "Mistral";
    public const string Ollama = "Ollama";
    public const string OllamaCloud = "OllamaCloud";

    public static string Normalize(string? value)
    {
        return value switch
        {
            ChatGptPremium => ChatGptPremium,
            OpenAi => OpenAi,
            Anthropic => Anthropic,
            Mistral => Mistral,
            Ollama => Ollama,
            OllamaCloud => OllamaCloud,
            _ => ChatGptPremium
        };
    }

    public static string ToDisplayName(string value)
    {
        return Normalize(value) switch
        {
            ChatGptPremium => "OpenAI OAuth",
            OpenAi => "OpenAI",
            Anthropic => "Anthropic",
            Mistral => "Mistral",
            Ollama => "Ollama",
            OllamaCloud => "Ollama Cloud",
            _ => "OpenAI OAuth"
        };
    }
}
