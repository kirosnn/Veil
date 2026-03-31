using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Veil.Interop;
using Windows.Gaming.Preview.GamesEnumeration;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed class GameDetectionService
{
    internal const string ManualListMode = "ManualList";
    internal const string HybridMode = "Hybrid";
    internal const string XboxFallbackMode = "XboxFallback";

    private static readonly TimeSpan CatalogRefreshInterval = TimeSpan.FromMinutes(10);
    private static readonly string[] GameDirectoryMarkers =
    [
        "\\steamapps\\common\\",
        "\\xboxgames\\",
        "\\riot games\\",
        "\\epic games\\",
        "\\gog galaxy\\games\\",
        "\\ubisoft game launcher\\games\\",
        "\\ea games\\",
        "\\battle.net\\",
        "\\games\\"
    ];
    private static readonly HashSet<string> ForegroundFallbackDenyList =
    [
        "applicationframehost",
        "browserhost",
        "chrome",
        "cmd",
        "code",
        "conhost",
        "devenv",
        "discord",
        "dwm",
        "epicgameslauncher",
        "explorer",
        "firefox",
        "launcher",
        "lockapp",
        "msedge",
        "obs64",
        "opera",
        "powershell",
        "pwsh",
        "searchhost",
        "shellexperiencehost",
        "slack",
        "steam",
        "steamwebhelper",
        "taskmgr",
        "teams",
        "terminal",
        "wezterm-gui",
        "windowsinternal.composableshell.experiences.textinput.inputapp",
        "windowsterminal",
        "wt"
    ];

    private readonly object _sync = new();
    private DateTime _lastCatalogRefreshUtc = DateTime.MinValue;
    private bool _catalogRefreshPending;
    private HashSet<string> _catalogExecutablePaths = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _catalogProcessNames = new(StringComparer.OrdinalIgnoreCase);

    internal void WarmUp()
    {
        EnsureCatalogRefreshScheduled(force: true);
    }

    internal static async Task<IReadOnlyList<string>> GetCatalogGameDisplayNamesAsync()
    {
        try
        {
            IReadOnlyList<GameListEntry> entries = await GameList.FindAllAsync().AsTask();
            return entries
                .Where(static entry => entry.Category is GameListCategory.ConfirmedBySystem or GameListCategory.ConfirmedByUser)
                .Select(static entry => CreateDisplayName(entry))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    internal bool IsConfiguredGameRunning(IReadOnlyList<string> configuredProcessNames)
        => GameProcessMonitor.IsAnyConfiguredGameRunning(configuredProcessNames);

    internal int? TryGetActiveGameProcessId(string detectionMode, IReadOnlyList<string> configuredProcessNames, ScreenBounds screen, IntPtr veilWindowHandle)
    {
        detectionMode = NormalizeDetectionMode(detectionMode);

        if (detectionMode != HybridMode)
        {
            return null;
        }

        EnsureCatalogRefreshScheduled(force: false);

        if (!TryGetForegroundProcessInfo([veilWindowHandle], out ForegroundProcessInfo processInfo, out _))
        {
            return null;
        }

        if (IsGameForegroundForScreen(processInfo, screen, configuredProcessNames))
        {
            return processInfo.ProcessId;
        }

        return null;
    }

    internal static string NormalizeDetectionMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return HybridMode;
        }

        if (string.Equals(value, HybridMode, StringComparison.Ordinal) ||
            string.Equals(value, XboxFallbackMode, StringComparison.Ordinal))
        {
            return HybridMode;
        }

        return ManualListMode;
    }

    internal bool TryGetForegroundProcessInfo(IReadOnlyCollection<IntPtr> ignoredWindowHandles, out ForegroundProcessInfo processInfo, out IntPtr foregroundWindow)
    {
        processInfo = default;
        foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero || ignoredWindowHandles.Contains(foregroundWindow))
        {
            return false;
        }

        if (!GetWindowRect(foregroundWindow, out Rect rect))
        {
            return false;
        }

        GetWindowThreadProcessId(foregroundWindow, out uint processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            string processName = GameProcessMonitor.NormalizeProcessName(process.ProcessName);
            string executablePath = string.Empty;

            try
            {
                executablePath = process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                executablePath = string.Empty;
            }

            processInfo = new ForegroundProcessInfo(process.Id, processName, executablePath, rect);
            return !string.IsNullOrWhiteSpace(processInfo.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    internal bool IsGameForegroundForScreen(ForegroundProcessInfo processInfo, ScreenBounds screen, IReadOnlyList<string> configuredProcessNames)
    {
        if (!IsWindowFullscreenLike(processInfo.WindowRect, screen))
        {
            return false;
        }

        return MatchesConfiguredList(processInfo.ProcessName, configuredProcessNames) ||
            IsCatalogMatch(processInfo) ||
            IsLikelyGameForeground(processInfo);
    }

    internal int? TryGetForegroundGameProcessIdForScreen(ForegroundProcessInfo processInfo, ScreenBounds screen, IReadOnlyList<string> configuredProcessNames)
        => IsGameForegroundForScreen(processInfo, screen, configuredProcessNames)
            ? processInfo.ProcessId
            : null;

    private static bool MatchesConfiguredList(string processName, IReadOnlyList<string> configuredProcessNames)
    {
        if (configuredProcessNames.Count == 0)
        {
            return false;
        }

        return configuredProcessNames
            .Select(GameProcessMonitor.NormalizeProcessName)
            .Any(configuredName => string.Equals(configuredName, processName, StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureCatalogRefreshScheduled(bool force)
    {
        lock (_sync)
        {
            if (_catalogRefreshPending)
            {
                return;
            }

            if (!force && DateTime.UtcNow - _lastCatalogRefreshUtc < CatalogRefreshInterval)
            {
                return;
            }

            _catalogRefreshPending = true;
            _lastCatalogRefreshUtc = DateTime.UtcNow;
        }

        _ = Task.Run(RefreshCatalogAsync);
    }

    private async Task RefreshCatalogAsync()
    {
        try
        {
            IReadOnlyList<GameListEntry> entries = await GameList.FindAllAsync().AsTask();
            HashSet<string> executablePaths = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> processNames = new(StringComparer.OrdinalIgnoreCase);

            foreach (GameListEntry entry in entries)
            {
                if (entry.Category is not GameListCategory.ConfirmedBySystem and not GameListCategory.ConfirmedByUser)
                {
                    continue;
                }

                string? launcherPath = null;
                try
                {
                    launcherPath = entry.LauncherExecutable?.Path;
                }
                catch
                {
                }

                if (string.IsNullOrWhiteSpace(launcherPath))
                {
                    continue;
                }

                executablePaths.Add(launcherPath);
                processNames.Add(GameProcessMonitor.NormalizeProcessName(Path.GetFileNameWithoutExtension(launcherPath)));
            }

            lock (_sync)
            {
                _catalogExecutablePaths = executablePaths;
                _catalogProcessNames = processNames;
            }
        }
        catch
        {
        }
        finally
        {
            lock (_sync)
            {
                _catalogRefreshPending = false;
            }
        }
    }

    private bool IsCatalogMatch(ForegroundProcessInfo processInfo)
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(processInfo.ExecutablePath) &&
                _catalogExecutablePaths.Contains(processInfo.ExecutablePath))
            {
                return true;
            }

            return _catalogProcessNames.Contains(processInfo.ProcessName);
        }
    }

    private static bool IsLikelyGameForeground(ForegroundProcessInfo processInfo)
    {
        if (ForegroundFallbackDenyList.Contains(processInfo.ProcessName))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(processInfo.ExecutablePath))
        {
            return false;
        }

        string normalizedPath = processInfo.ExecutablePath.Replace('/', '\\');
        return GameDirectoryMarkers.Any(marker => normalizedPath.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsWindowFullscreenLike(Rect rect, ScreenBounds screen)
    {
        const int tolerance = 10;
        int screenWidth = screen.Right - screen.Left;
        int screenHeight = screen.Bottom - screen.Top;
        int windowWidth = rect.Right - rect.Left;
        int windowHeight = rect.Bottom - rect.Top;

        bool coversWidth = rect.Left <= screen.Left + tolerance && rect.Right >= screen.Right - tolerance;
        bool coversHeight = rect.Top <= screen.Top + tolerance && rect.Bottom >= screen.Bottom - tolerance;
        bool largeEnough = windowWidth >= (int)(screenWidth * 0.9) && windowHeight >= (int)(screenHeight * 0.88);

        return largeEnough && coversWidth && coversHeight;
    }

    private static string CreateDisplayName(GameListEntry entry)
    {
        string displayName = entry.DisplayInfo?.DisplayName ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName.Trim();
        }

        try
        {
            string? launcherPath = entry.LauncherExecutable?.Path;
            if (!string.IsNullOrWhiteSpace(launcherPath))
            {
                return Path.GetFileNameWithoutExtension(launcherPath);
            }
        }
        catch
        {
        }

        return entry.TitleId ?? string.Empty;
    }

    internal readonly record struct ForegroundProcessInfo(int ProcessId, string ProcessName, string ExecutablePath, Rect WindowRect);
}
