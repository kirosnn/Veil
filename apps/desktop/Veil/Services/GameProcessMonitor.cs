using System.Diagnostics;

namespace Veil.Services;

internal static class GameProcessMonitor
{
    internal static bool IsAnyConfiguredGameRunning(IReadOnlyList<string> configuredProcessNames)
    {
        if (configuredProcessNames.Count == 0)
        {
            return false;
        }

        HashSet<string> configuredNames = configuredProcessNames
            .Select(NormalizeProcessName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (configuredNames.Count == 0)
        {
            return false;
        }

        try
        {
            foreach (string configuredName in configuredNames)
            {
                Process[] matchingProcesses = Process.GetProcessesByName(configuredName);
                foreach (Process process in matchingProcesses)
                {
                    try
                    {
                        return true;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    internal static string NormalizeProcessName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4].Trim().ToLowerInvariant()
            : trimmed.ToLowerInvariant();
    }
}
