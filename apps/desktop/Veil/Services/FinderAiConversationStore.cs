using System.Text.Json;
using Veil.Diagnostics;

namespace Veil.Services;

internal sealed class FinderAiConversationStore
{
    private const int MaxSessions = 24;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string _filePath;
    private readonly object _sync = new();

    internal FinderAiConversationStore()
    {
        _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Veil",
            "ai",
            "finder-conversations.json");
    }

    internal IReadOnlyList<FinderAiConversationSession> LoadSessions()
    {
        lock (_sync)
        {
            return LoadSessionsUnsafe();
        }
    }

    internal void UpsertSession(FinderAiConversationSession session)
    {
        lock (_sync)
        {
            var sessions = LoadSessionsUnsafe().ToList();
            int existingIndex = sessions.FindIndex(item => string.Equals(item.Id, session.Id, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                sessions[existingIndex] = session;
            }
            else
            {
                sessions.Add(session);
            }

            PersistSessionsUnsafe(sessions);
        }
    }

    internal void DeleteSession(string sessionId)
    {
        lock (_sync)
        {
            var sessions = LoadSessionsUnsafe()
                .Where(item => !string.Equals(item.Id, sessionId, StringComparison.Ordinal))
                .ToArray();
            PersistSessionsUnsafe(sessions);
        }
    }

    private IReadOnlyList<FinderAiConversationSession> LoadSessionsUnsafe()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            FinderAiConversationSession[]? sessions = JsonSerializer.Deserialize<FinderAiConversationSession[]>(
                File.ReadAllText(_filePath),
                JsonOptions);
            return (sessions ?? [])
                .Where(static session => session.Turns is { Count: > 0 })
                .OrderByDescending(static session => session.UpdatedAtUtc)
                .ToArray();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to load Veil Halo conversations.", ex);
            return [];
        }
    }

    private void PersistSessionsUnsafe(IEnumerable<FinderAiConversationSession> sessions)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            FinderAiConversationSession[] payload = sessions
                .Where(static session => session.Turns is { Count: > 0 })
                .OrderByDescending(static session => session.UpdatedAtUtc)
                .Take(MaxSessions)
                .ToArray();
            File.WriteAllText(_filePath, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to persist Veil Halo conversations.", ex);
        }
    }
}

internal sealed record FinderAiConversationSession(
    string Id,
    string Title,
    string Provider,
    string Model,
    DateTime UpdatedAtUtc,
    IReadOnlyList<AiAgentTurn> Turns);
