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
    private static readonly HashSet<string> RemoteGamingProcessNames =
    [
        "parsec",
        "parsecd",
        "moonlight",
        "sunshine",
        "steamlink",
        "steamstreamingclient"
    ];
    private static readonly HashSet<string> CaptureOverlayProcessNames =
    [
        "flameshot",
        "gamebar",
        "gamebarftserver",
        "gamingoverlay",
        "greenshot",
        "lightshot",
        "picpick",
        "screenclippinghost",
        "screenpresso",
        "screensketch",
        "sharex",
        "snagit32",
        "snagiteditor",
        "snippingtool"
    ];
    private static readonly HashSet<string> ShellHostProcessNames =
    [
        "applicationframehost",
        "explorer",
        "searchhost",
        "shellexperiencehost",
        "startmenuexperiencehost",
        "textinputhost",
        "windowsinternal.composableshell.experiences.textinput.inputapp"
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

    internal bool IsConfiguredGameRunning(IReadOnlySet<string> configuredProcessNames)
        => GameProcessMonitor.IsAnyConfiguredGameRunning(configuredProcessNames);

    internal int? TryGetActiveGameProcessId(string detectionMode, IReadOnlyList<string> configuredProcessNames, ScreenBounds screen, IntPtr veilWindowHandle)
    {
        detectionMode = NormalizeDetectionMode(detectionMode);

        if (detectionMode != HybridMode)
        {
            return null;
        }

        EnsureCatalogRefreshScheduled(force: false);

        if (!TryGetVisibilityProcessInfoForScreen([veilWindowHandle], screen, out ForegroundProcessInfo processInfo, out _))
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

        if (IsDesktopShellWindow(foregroundWindow))
        {
            return false;
        }

        return TryCreateForegroundProcessInfo(foregroundWindow, out processInfo);
    }

    internal bool TryGetVisibilityProcessInfoForScreen(
        IReadOnlyCollection<IntPtr> ignoredWindowHandles,
        ScreenBounds screen,
        out ForegroundProcessInfo processInfo,
        out IntPtr foregroundWindow)
    {
        processInfo = default;
        foregroundWindow = IntPtr.Zero;

        List<ForegroundProcessInfo> candidates = [];
        HashSet<IntPtr> seenWindows = [];

        if (TryGetForegroundProcessInfo(ignoredWindowHandles, out ForegroundProcessInfo currentForeground, out IntPtr currentForegroundWindow))
        {
            candidates.Add(currentForeground);
            seenWindows.Add(currentForegroundWindow);
        }

        EnumWindows((hwnd, _) =>
        {
            if (hwnd == IntPtr.Zero || ignoredWindowHandles.Contains(hwnd) || seenWindows.Contains(hwnd))
            {
                return true;
            }

            if (!TryCreateForegroundProcessInfo(hwnd, out ForegroundProcessInfo candidate))
            {
                return true;
            }

            candidates.Add(candidate);
            seenWindows.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        if (!TrySelectVisibilityProcessInfoForScreen(candidates, screen, out processInfo))
        {
            return false;
        }

        foregroundWindow = processInfo.WindowHandle;
        return foregroundWindow != IntPtr.Zero;
    }

    internal static bool TrySelectVisibilityProcessInfoForScreen(
        IEnumerable<ForegroundProcessInfo> candidates,
        ScreenBounds screen,
        out ForegroundProcessInfo processInfo)
    {
        foreach (ForegroundProcessInfo candidate in candidates)
        {
            if (!WindowHelper.IntersectsScreen(candidate.WindowRect, screen))
            {
                continue;
            }

            if (ShouldIgnoreForegroundWindowForVisibility(candidate))
            {
                continue;
            }

            processInfo = candidate;
            return true;
        }

        processInfo = default;
        return false;
    }

    internal bool IsGameForegroundForScreen(ForegroundProcessInfo processInfo, ScreenBounds screen, IReadOnlyList<string> configuredProcessNames)
        => IsGameForegroundForScreen(processInfo, screen, GameProcessMonitor.CreateNormalizedProcessNameSet(configuredProcessNames));

    internal bool IsGameForegroundForScreen(ForegroundProcessInfo processInfo, ScreenBounds screen, IReadOnlySet<string> configuredProcessNames)
    {
        if (!IsWindowFullscreenLike(processInfo.WindowRect, screen))
        {
            return false;
        }

        return MatchesConfiguredList(processInfo.ProcessName, configuredProcessNames) ||
            IsRemoteGamingProcess(processInfo.ProcessName) ||
            IsCatalogMatch(processInfo) ||
            IsLikelyGameForeground(processInfo);
    }

    internal int? TryGetForegroundGameProcessIdForScreen(ForegroundProcessInfo processInfo, ScreenBounds screen, IReadOnlyList<string> configuredProcessNames)
        => TryGetForegroundGameProcessIdForScreen(processInfo, screen, GameProcessMonitor.CreateNormalizedProcessNameSet(configuredProcessNames));

    internal int? TryGetForegroundGameProcessIdForScreen(ForegroundProcessInfo processInfo, ScreenBounds screen, IReadOnlySet<string> configuredProcessNames)
        => IsGameForegroundForScreen(processInfo, screen, configuredProcessNames)
            ? processInfo.ProcessId
            : null;

    internal bool IsForegroundWindowFullscreenForScreen(ForegroundProcessInfo processInfo, ScreenBounds screen)
        => !ShouldIgnoreForegroundWindowForVisibility(processInfo) &&
            IsWindowFullscreenLike(processInfo.WindowRect, screen);

    private static bool MatchesConfiguredList(string processName, IReadOnlySet<string> configuredProcessNames)
    {
        if (configuredProcessNames.Count == 0)
        {
            return false;
        }

        return configuredProcessNames.Contains(processName);
    }

    internal static bool IsRemoteGamingProcess(string? processName)
    {
        string normalizedProcessName = GameProcessMonitor.NormalizeProcessName(processName);
        return !string.IsNullOrWhiteSpace(normalizedProcessName) &&
            RemoteGamingProcessNames.Contains(normalizedProcessName);
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

        return IsLikelyGameInstallationPath(processInfo.ExecutablePath);
    }

    internal static bool IsLikelyGameInstallationPath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        string normalizedPath = executablePath.Replace('/', '\\');
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

    internal static bool ShouldIgnoreForegroundWindowClassForGameDetection(string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        return className is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32";
    }

    internal static bool ShouldIgnoreForegroundWindowForVisibility(ForegroundProcessInfo processInfo)
    {
        if (processInfo.WindowHandle == IntPtr.Zero)
        {
            return true;
        }

        if (processInfo.IsCloaked)
        {
            return true;
        }

        if (ShouldIgnoreForegroundWindowClassForGameDetection(processInfo.WindowClassName))
        {
            return true;
        }

        if (!processInfo.HasAppLikePresence)
        {
            return true;
        }

        if (IsCaptureOverlayProcess(processInfo.ProcessName, processInfo.ExecutablePath))
        {
            return true;
        }

        if (IsHostedShellOverlay(processInfo))
        {
            return true;
        }

        return false;
    }

    private static bool IsDesktopShellWindow(IntPtr hwnd)
    {
        if (hwnd == GetShellWindow())
        {
            return true;
        }

        string className = GetWindowClassName(hwnd);
        if (ShouldIgnoreForegroundWindowClassForGameDetection(className))
        {
            return true;
        }

        return HasShellDesktopChild(hwnd);
    }

    private static bool TryCreateForegroundProcessInfo(IntPtr hwnd, out ForegroundProcessInfo processInfo)
    {
        processInfo = default;

        if (hwnd == IntPtr.Zero || IsDesktopShellWindow(hwnd))
        {
            return false;
        }

        if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
        {
            return false;
        }

        if (!GetWindowRect(hwnd, out Rect rect))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            string processName = GameProcessMonitor.NormalizeProcessName(process.ProcessName);
            if (string.IsNullOrWhiteSpace(processName))
            {
                return false;
            }

            string executablePath = string.Empty;
            try
            {
                executablePath = process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                executablePath = string.Empty;
            }

            string className = GetWindowClassName(hwnd);
            string windowTitle = GetWindowTitle(hwnd);
            int exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
            bool isCloaked = IsWindowCloaked(hwnd);
            bool hasAppLikePresence = HasAppLikeWindowPresence(hwnd, exStyle);

            processInfo = new ForegroundProcessInfo(
                process.Id,
                processName,
                executablePath,
                rect,
                hwnd,
                className,
                windowTitle,
                exStyle,
                hasAppLikePresence,
                isCloaked);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasShellDesktopChild(IntPtr hwnd)
    {
        IntPtr defView = FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero)
        {
            return true;
        }

        IntPtr listView = FindWindowExW(hwnd, IntPtr.Zero, "SysListView32", null);
        return listView != IntPtr.Zero;
    }

    private static bool HasAppLikeWindowPresence(IntPtr hwnd, int exStyle)
    {
        if (!WindowSwitcherService.HasSwitchableExtendedStyles(exStyle))
        {
            return false;
        }

        IntPtr owner = GetWindow(hwnd, GW_OWNER);
        if (owner != IntPtr.Zero && (exStyle & WS_EX_APPWINDOW) == 0)
        {
            return false;
        }

        IntPtr rootOwner = GetAncestor(hwnd, GA_ROOTOWNER);
        if (rootOwner == IntPtr.Zero)
        {
            rootOwner = hwnd;
        }

        IntPtr walk = rootOwner;
        while (true)
        {
            IntPtr lastPopup = GetLastActivePopup(walk);
            if (lastPopup == IntPtr.Zero || lastPopup == walk)
            {
                break;
            }

            if (IsWindowVisible(lastPopup))
            {
                walk = lastPopup;
                break;
            }

            walk = lastPopup;
        }

        return walk == hwnd || rootOwner == hwnd;
    }

    private static bool IsCaptureOverlayProcess(string processName, string executablePath)
    {
        if (CaptureOverlayProcessNames.Contains(processName))
        {
            return true;
        }

        string executableName = string.IsNullOrWhiteSpace(executablePath)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(executablePath);
        return !string.IsNullOrWhiteSpace(executableName) &&
            CaptureOverlayProcessNames.Contains(GameProcessMonitor.NormalizeProcessName(executableName));
    }

    private static bool IsHostedShellOverlay(ForegroundProcessInfo processInfo)
    {
        if (!ShellHostProcessNames.Contains(processInfo.ProcessName))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(processInfo.WindowTitle) ||
            !processInfo.HasAppLikePresence;
    }

    private static bool IsWindowCloaked(IntPtr hwnd)
    {
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out uint cloaked, sizeof(uint)) == 0 &&
            cloaked != 0;
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var classNameBuffer = new char[256];
        int classNameLength = GetClassNameW(hwnd, classNameBuffer, classNameBuffer.Length);
        return classNameLength <= 0
            ? string.Empty
            : new string(classNameBuffer, 0, classNameLength);
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var titleBuffer = new char[512];
        int titleLength = GetWindowTextW(hwnd, titleBuffer, titleBuffer.Length);
        return titleLength <= 0
            ? string.Empty
            : new string(titleBuffer, 0, titleLength);
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

    internal readonly record struct ForegroundProcessInfo(
        int ProcessId,
        string ProcessName,
        string ExecutablePath,
        Rect WindowRect,
        IntPtr WindowHandle = default,
        string WindowClassName = "",
        string WindowTitle = "",
        int ExtendedStyles = 0,
        bool HasAppLikePresence = true,
        bool IsCloaked = false);
}
