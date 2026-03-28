using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed record InstalledApp(string Name, string AppId, string MatchToken, string SearchToken);

internal static class InstalledAppService
{
    private const int MaxLoadAttempts = 3;
    private static Task<IReadOnlyList<InstalledApp>>? _cachedAppsTask;
    private static readonly Lock SyncRoot = new();

    internal static Task<IReadOnlyList<InstalledApp>> GetAppsAsync()
    {
        lock (SyncRoot)
        {
            _cachedAppsTask ??= LoadAppsAsync();
            return _cachedAppsTask;
        }
    }

    internal static Task<IReadOnlyList<InstalledApp>> RefreshAppsAsync()
    {
        lock (SyncRoot)
        {
            _cachedAppsTask = LoadAppsAsync();
            return _cachedAppsTask;
        }
    }

    internal static Task PreloadAsync() => GetAppsAsync();

    private static async Task<IReadOnlyList<InstalledApp>> LoadAppsAsync()
    {
        Exception? lastError = null;

        for (int attempt = 1; attempt <= MaxLoadAttempts; attempt++)
        {
            try
            {
                var apps = await LoadAppsOnceAsync();
                if (apps.Count > 0 || attempt == MaxLoadAttempts)
                {
                    return apps;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt == MaxLoadAttempts)
                {
                    break;
                }
            }

            await Task.Delay(200 * attempt);
        }

        lock (SyncRoot)
        {
            _cachedAppsTask = null;
        }

        throw lastError ?? new InvalidOperationException("Failed to enumerate installed applications.");
    }

    private static async Task<IReadOnlyList<InstalledApp>> LoadAppsOnceAsync()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; [Console]::ErrorEncoding = [System.Text.Encoding]::UTF8; Get-StartApps | Sort-Object Name | ConvertTo-Json -Compress\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            }
        };

        process.Start();

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Failed to enumerate installed applications." : error.Trim());
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        using var document = JsonDocument.Parse(output);
        var apps = new List<InstalledApp>();

        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in document.RootElement.EnumerateArray())
            {
                AddApp(apps, item);
            }
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            AddApp(apps, document.RootElement);
        }

        return apps
            .Where(app => !string.IsNullOrWhiteSpace(app.Name) && !string.IsNullOrWhiteSpace(app.AppId))
            .DistinctBy(app => app.AppId)
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    internal static void Launch(InstalledApp app)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"shell:AppsFolder\\{app.AppId}",
            UseShellExecute = true
        });
    }

    internal static void LaunchOrActivate(InstalledApp app)
    {
        IntPtr foregroundWindow = GetForegroundWindow();
        IntPtr bestWindow = FindBestWindow(app);

        if (bestWindow != IntPtr.Zero)
        {
            if (bestWindow == foregroundWindow)
            {
                Launch(app);
                return;
            }

            if (IsIconic(bestWindow))
            {
                ShowWindowNative(bestWindow, SW_RESTORE);
            }

            SetForegroundWindow(bestWindow);
            return;
        }

        Launch(app);
    }

    private static void AddApp(ICollection<InstalledApp> apps, JsonElement item)
    {
        string? name = item.TryGetProperty("Name", out var nameValue) ? nameValue.GetString() : null;
        string? appId = item.TryGetProperty("AppID", out var appIdValue) ? appIdValue.GetString() : null;

        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(appId))
        {
            string normalizedName = name.Trim();
            string normalizedAppId = appId.Trim();
            apps.Add(new InstalledApp(
                normalizedName,
                normalizedAppId,
                BuildMatchToken(normalizedName, normalizedAppId),
                Normalize(normalizedName)));
        }
    }

    private static IntPtr FindBestWindow(InstalledApp app)
    {
        IntPtr bestWindow = IntPtr.Zero;
        int bestScore = 0;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            int score = ScoreWindow(hwnd, app);
            if (score > bestScore)
            {
                bestScore = score;
                bestWindow = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return bestWindow;
    }

    private static int ScoreWindow(IntPtr hwnd, InstalledApp app)
    {
        GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return 0;
        }

        string windowTitle = GetWindowTitle(hwnd);
        string appName = Normalize(app.Name);
        string matchToken = Normalize(app.MatchToken);
        int score = 0;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            string processName = Normalize(process.ProcessName);
            string fileDescription = Normalize(process.MainModule?.FileVersionInfo?.FileDescription);

            if (!string.IsNullOrWhiteSpace(matchToken) && processName == matchToken)
            {
                score += 120;
            }

            if (!string.IsNullOrWhiteSpace(matchToken) && fileDescription.Contains(matchToken, StringComparison.OrdinalIgnoreCase))
            {
                score += 80;
            }

            if (!string.IsNullOrWhiteSpace(appName) && fileDescription.Contains(appName, StringComparison.OrdinalIgnoreCase))
            {
                score += 80;
            }

            if (!string.IsNullOrWhiteSpace(appName) && processName.Contains(appName, StringComparison.OrdinalIgnoreCase))
            {
                score += 60;
            }
        }
        catch
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(appName) && windowTitle.Contains(appName, StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        return score;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var buffer = new char[512];
        int length = GetWindowTextW(hwnd, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    private static string BuildMatchToken(string name, string appId)
    {
        string normalizedName = Normalize(name);
        string normalizedId = appId.Trim();

        if (normalizedId.Contains('\\') || normalizedId.Contains('/'))
        {
            return Normalize(Path.GetFileNameWithoutExtension(normalizedId));
        }

        if (normalizedId.Contains('!'))
        {
            normalizedId = normalizedId.Split('!')[0];
        }

        string[] parts = normalizedId.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 0)
        {
            return Normalize(parts[^1]);
        }

        return normalizedName;
    }

    internal static string NormalizeText(string? value) => Normalize(value);

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (char character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
