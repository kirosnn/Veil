using System.Diagnostics;

namespace Veil.Services;

internal static class GameProcessMonitor
{
    internal static HashSet<string> CreateNormalizedProcessNameSet(IReadOnlyList<string> configuredProcessNames)
    {
        if (configuredProcessNames.Count == 0)
        {
            return [];
        }

        return configuredProcessNames
            .Select(NormalizeProcessName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsAnyConfiguredGameRunning(IReadOnlyList<string> configuredProcessNames)
        => IsAnyConfiguredGameRunning(CreateNormalizedProcessNameSet(configuredProcessNames));

    internal static bool IsAnyConfiguredGameRunning(IReadOnlySet<string> configuredProcessNames)
    {
        if (configuredProcessNames.Count == 0)
        {
            return false;
        }

        try
        {
            foreach (string configuredName in configuredProcessNames)
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
