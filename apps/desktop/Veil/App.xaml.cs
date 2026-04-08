using System.Diagnostics;
using Microsoft.UI.Xaml;
using Veil.Configuration;
using Veil.Diagnostics;
using Veil.Interop;
using Veil.Services;
using Veil.Windows;

namespace Veil;

public partial class App : Application
{
    private static readonly TimeSpan TopBarActivityInterval = TimeSpan.FromMilliseconds(1000);
    private readonly AppSettings _settings;
    private readonly GameDetectionService _gameDetectionService = new();
    private readonly Dictionary<string, TopBarWindow> _topBarWindows = new(StringComparer.OrdinalIgnoreCase);
    private DesktopIconVisibilityService? _desktopIconVisibilityService;
    private DesktopContextMenuService? _desktopContextMenu;
    private TrayIconService? _trayIcon;
    private AltTabHookService? _altTabHookService;
    private AltTabWindow? _altTabWindow;
    private IReadOnlyList<WindowSwitchEntry> _windowSwitchEntries = [];
    private int _windowSwitchSelectedIndex = -1;
    private DispatcherTimer? _monitorRefreshTimer;
    private DispatcherTimer? _topBarActivityTimer;
    private string _lastTopologySignature = string.Empty;
    private bool _isSyncingTopBars;
    private int _emptyMonitorRefreshCount;
    private bool _startTopBarsHiddenUntilReady;

    public App()
    {
        InitializeComponent();
        _settings = AppSettings.Current;
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        _settings.Changed += OnSettingsChanged;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            KillOtherInstances();
            PerformanceLogger.Start();
            AppLogger.Info("Application launch started.");
            _startTopBarsHiddenUntilReady = StartupService.IsStartupLaunch(args.Arguments);
            try
            {
                _settings.Save();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to persist settings during launch.", ex);
            }

            if (!StartupService.IsEnabled())
            {
                try
                {
                    StartupService.Enable();
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Failed to enable startup registration.", ex);
                }
            }

            _desktopIconVisibilityService = new DesktopIconVisibilityService(new WindowsDesktopIconVisibilityBridge());
            _desktopIconVisibilityService.ApplyLaunchState();

            _trayIcon = new TrayIconService();
            _trayIcon.ShowRequested += OnTrayShowRequested;
            _trayIcon.SettingsRequested += OnTraySettingsRequested;
            _trayIcon.QuitRequested += OnTrayQuitRequested;
            _trayIcon.Initialize();

            InitializeAltTabSwitcher();
            _gameDetectionService.WarmUp();
            _ = InstalledAppService.PreloadAsync();
            SyncTopBarWindows(true);
            StartMonitorRefresh();
            StartTopBarActivityRefresh();

            if (_settings.IsFirstLaunch)
            {
                GetPreferredTopBarWindow()?.OpenSettings();
            }
        }
        catch (Exception ex)
        {
            try
            {
                _desktopIconVisibilityService?.RestoreLaunchState();
                _desktopIconVisibilityService = null;
            }
            catch (Exception restoreEx)
            {
                AppLogger.Error("Failed to restore desktop icons after launch error.", restoreEx);
            }

            AppLogger.Error("Fatal error during launch.", ex);
            throw;
        }
    }

    private static void KillOtherInstances()
    {
        int currentPid = Environment.ProcessId;
        string currentName = Process.GetCurrentProcess().ProcessName;
        List<int> previousInstanceIds = [];

        foreach (var process in Process.GetProcessesByName(currentName))
        {
            try
            {
                if (process.Id != currentPid)
                {
                    previousInstanceIds.Add(process.Id);
                    AppLogger.Info($"Killing previous instance (pid {process.Id}).");
                    process.Kill();
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        if (previousInstanceIds.Count == 0)
        {
            return;
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            bool hasRemainingInstance = false;

            foreach (var process in Process.GetProcessesByName(currentName))
            {
                try
                {
                    if (process.Id != currentPid)
                    {
                        hasRemainingInstance = true;
                        break;
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (!hasRemainingInstance)
            {
                return;
            }

            Thread.Sleep(250);
        }

        AppLogger.Error("Previous Veil instance is still shutting down after timeout.");
    }

    private void OnTrayShowRequested()
    {
        TopBarWindow? preferredWindow = GetPreferredTopBarWindow();
        if (preferredWindow is null)
        {
            return;
        }

        preferredWindow.DispatcherQueue.TryEnqueue(() =>
        {
            preferredWindow.Activate();
        });
    }

    private void OnTraySettingsRequested()
    {
        TopBarWindow? preferredWindow = GetPreferredTopBarWindow();
        preferredWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            preferredWindow.OpenSettings();
        });
    }

    private void OnTrayQuitRequested()
    {
        AppLogger.Info("Tray quit requested.");
        _monitorRefreshTimer?.Stop();
        _monitorRefreshTimer = null;
        _topBarActivityTimer?.Stop();
        _topBarActivityTimer = null;
        DisposeAltTabSwitcher();
        _desktopContextMenu?.Dispose();
        _desktopContextMenu = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        CloseAllTopBarWindows();
        _desktopIconVisibilityService?.RestoreLaunchState();
        _desktopIconVisibilityService = null;
        PerformanceLogger.Stop();
    }

    private void OnSettingsChanged()
    {
        SyncTopBarWindows();
    }

    private void StartMonitorRefresh()
    {
        _monitorRefreshTimer?.Stop();
        _monitorRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _monitorRefreshTimer.Tick += OnMonitorRefreshTick;
        _monitorRefreshTimer.Start();
    }

    private void OnMonitorRefreshTick(object? sender, object e)
    {
        try
        {
            SyncTopBarWindows();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Monitor refresh tick failed.", ex);
        }
    }

    private void StartTopBarActivityRefresh()
    {
        _topBarActivityTimer?.Stop();
        _topBarActivityTimer = new DispatcherTimer
        {
            Interval = TopBarActivityInterval
        };
        _topBarActivityTimer.Tick += OnTopBarActivityTick;
        _topBarActivityTimer.Start();
        RefreshTopBarActivityState();
    }

    private void OnTopBarActivityTick(object? sender, object e)
    {
        try
        {
            RefreshTopBarActivityState();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Top bar activity refresh tick failed.", ex);
        }
    }

    private void RefreshTopBarActivityState()
    {
        try
        {
            if (_isSyncingTopBars || _topBarWindows.Count == 0)
            {
                return;
            }

            string detectionMode = GameDetectionService.NormalizeDetectionMode(_settings.GameDetectionMode);
            IReadOnlyList<string> configuredProcessNames = _settings.GameProcessNames;
            bool configuredGameRunning = _gameDetectionService.IsConfiguredGameRunning(configuredProcessNames);
            IntPtr[] ignoredWindows = _topBarWindows.Values
                .Select(static window => window.WindowHandle)
                .Where(static handle => handle != IntPtr.Zero)
                .ToArray();
            GameDetectionService.ForegroundProcessInfo foregroundProcessInfo = default;

            bool hasForegroundProcess = detectionMode == GameDetectionService.HybridMode &&
                _gameDetectionService.TryGetForegroundProcessInfo(ignoredWindows, out foregroundProcessInfo, out _);

            foreach (TopBarWindow topBarWindow in _topBarWindows.Values)
            {
                try
                {
                    bool gameRunning = configuredGameRunning;
                    int? activeGameProcessId = null;
                    bool shouldHideForForegroundWindow = false;

                    if (hasForegroundProcess)
                    {
                        activeGameProcessId = _gameDetectionService.TryGetForegroundGameProcessIdForScreen(
                            foregroundProcessInfo,
                            topBarWindow.ScreenBounds,
                            configuredProcessNames);
                        shouldHideForForegroundWindow = activeGameProcessId.HasValue;

                        if (!gameRunning)
                        {
                            gameRunning = activeGameProcessId.HasValue;
                        }
                    }

                    topBarWindow.ApplyActivityState(gameRunning, shouldHideForForegroundWindow, activeGameProcessId);
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Failed to refresh top bar activity state for {topBarWindow.ScreenBounds}.", ex);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to refresh top bar activity state.", ex);
        }
    }

    private void SyncTopBarWindows(bool force = false)
    {
        if (_isSyncingTopBars)
        {
            return;
        }

        _isSyncingTopBars = true;

        try
        {
            List<MonitorInfo2> monitors = MonitorService.GetAllMonitors();
            if (monitors.Count == 0)
            {
                _emptyMonitorRefreshCount++;
                AppLogger.Error($"Monitor enumeration returned 0 displays (attempt {_emptyMonitorRefreshCount}).");

                if (_topBarWindows.Count > 0 && _emptyMonitorRefreshCount < 3)
                {
                    return;
                }

                AppLogger.Error("Closing all top bar windows because no displays were detected repeatedly.");
                CloseAllTopBarWindows("monitor-enumeration-empty");
                _lastTopologySignature = string.Empty;
                return;
            }

            _emptyMonitorRefreshCount = 0;
            string topologySignature = string.Join(
                "|",
                monitors.Select(static monitor =>
                    $"{monitor.Id}:{monitor.Bounds.Left},{monitor.Bounds.Top},{monitor.Bounds.Right},{monitor.Bounds.Bottom}:{monitor.IsPrimary}"));
            bool topologyChanged = force || !string.Equals(_lastTopologySignature, topologySignature, StringComparison.Ordinal);

            IReadOnlyList<string> enabledMonitorIds = _settings.ResolveTopBarMonitorIds(monitors);
            if (enabledMonitorIds.Count == 0)
            {
                AppLogger.Error("Top bar monitor resolution returned no eligible displays.");
                CloseAllTopBarWindows("monitor-resolution-empty");
                _lastTopologySignature = topologySignature;
                return;
            }

            string hotkeyOwnerId = monitors
                .FirstOrDefault(monitor => monitor.IsPrimary && enabledMonitorIds.Contains(monitor.Id, StringComparer.OrdinalIgnoreCase))?.Id
                ?? enabledMonitorIds.FirstOrDefault()
                ?? monitors[0].Id;

            if (topologyChanged)
            {
                AppLogger.Info($"Monitor topology changed to {topologySignature}.");
                CloseAllTopBarWindows("topology-changed");
                _lastTopologySignature = topologySignature;
            }

            foreach (string existingMonitorId in _topBarWindows.Keys.ToArray())
            {
                if (enabledMonitorIds.Contains(existingMonitorId, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                AppLogger.Info($"Closing top bar for disabled monitor {existingMonitorId}.");
                try
                {
                    _topBarWindows[existingMonitorId].Close();
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Failed to close top bar for disabled monitor {existingMonitorId}.", ex);
                }
                finally
                {
                    _topBarWindows.Remove(existingMonitorId);
                }
            }

            foreach (MonitorInfo2 monitor in monitors.Where(monitor => enabledMonitorIds.Contains(monitor.Id, StringComparer.OrdinalIgnoreCase)))
            {
                bool ownsGlobalHotkeys = string.Equals(monitor.Id, hotkeyOwnerId, StringComparison.OrdinalIgnoreCase);

                if (!_topBarWindows.TryGetValue(monitor.Id, out TopBarWindow? topBarWindow))
                {
                    try
                    {
                        TopBarWindow createdWindow = new(
                            monitor.Id,
                            monitor.ToScreenBounds(),
                            ownsGlobalHotkeys,
                            _startTopBarsHiddenUntilReady);
                        createdWindow.Activate();
                        _topBarWindows[monitor.Id] = createdWindow;
                        AppLogger.Info($"TopBarWindow activated for {monitor.Id}.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error($"Failed to create top bar for {monitor.Id}.", ex);
                    }

                    continue;
                }

                try
                {
                    topBarWindow.UpdateScreenBounds(monitor.ToScreenBounds());
                    topBarWindow.SetOwnsGlobalHotkeys(ownsGlobalHotkeys);
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Failed to refresh top bar ownership for {monitor.Id}.", ex);
                }
            }

            if (_topBarWindows.Count > 0)
            {
                _startTopBarsHiddenUntilReady = false;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to sync top bar windows.", ex);
        }
        finally
        {
            _isSyncingTopBars = false;
        }

        RefreshTopBarActivityState();
    }

    private TopBarWindow? GetPreferredTopBarWindow()
    {
        if (_topBarWindows.Count == 0)
        {
            return null;
        }

        List<MonitorInfo2> monitors = MonitorService.GetAllMonitors();
        IReadOnlyList<string> enabledMonitorIds = _settings.ResolveTopBarMonitorIds(monitors);
        string? preferredMonitorId = monitors
            .FirstOrDefault(monitor => monitor.IsPrimary && enabledMonitorIds.Contains(monitor.Id, StringComparer.OrdinalIgnoreCase))?.Id
            ?? enabledMonitorIds.FirstOrDefault();

        return preferredMonitorId != null && _topBarWindows.TryGetValue(preferredMonitorId, out TopBarWindow? preferredWindow)
            ? preferredWindow
            : _topBarWindows.Values.FirstOrDefault();
    }

    private void CloseAllTopBarWindows()
        => CloseAllTopBarWindows("unspecified");

    private void CloseAllTopBarWindows(string reason)
    {
        AppLogger.Info($"Closing all top bar windows. Reason: {reason}.");
        foreach (TopBarWindow topBarWindow in _topBarWindows.Values.ToArray())
        {
            try
            {
                topBarWindow.Close();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to close a top bar window.", ex);
            }
        }

        _topBarWindows.Clear();
    }

    private void InitializeDesktopContextMenu()
    {
        var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dispatcherQueue is null)
        {
            return;
        }

        _desktopContextMenu = new DesktopContextMenuService(dispatcherQueue);
        _desktopContextMenu.SettingsRequested += OnTraySettingsRequested;
        _desktopContextMenu.Initialize();
    }

    private void InitializeAltTabSwitcher()
    {
        var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dispatcherQueue is null)
        {
            return;
        }

        _altTabWindow = new AltTabWindow();
        _altTabWindow.Initialize();

        _altTabHookService = new AltTabHookService(dispatcherQueue);
        _altTabHookService.SwitchStarted += OnAltTabSwitchStarted;
        _altTabHookService.SwitchStepped += OnAltTabSwitchStepped;
        _altTabHookService.SwitchCommitted += OnAltTabSwitchCommitted;
        _altTabHookService.SwitchCanceled += OnAltTabSwitchCanceled;
        _altTabHookService.Initialize();
    }

    private void DisposeAltTabSwitcher()
    {
        _altTabHookService?.Dispose();
        _altTabHookService = null;

        if (_altTabWindow is not null)
        {
            _altTabWindow.HideSwitcher();
            _altTabWindow.Close();
            _altTabWindow = null;
        }

        _windowSwitchEntries = [];
        _windowSwitchSelectedIndex = -1;
    }

    private void OnAltTabSwitchStarted(bool moveForward)
    {
        _windowSwitchEntries = WindowSwitcherService.GetSwitchableWindows();
        if (_windowSwitchEntries.Count < 2)
        {
            _altTabHookService?.ResetSession();
            _windowSwitchEntries = [];
            _windowSwitchSelectedIndex = -1;
            return;
        }

        _windowSwitchSelectedIndex = moveForward ? 1 : _windowSwitchEntries.Count - 1;
        ShowAltTabSwitcher();
    }

    private void OnAltTabSwitchStepped(bool moveForward)
    {
        if (_windowSwitchEntries.Count < 2)
        {
            OnAltTabSwitchStarted(moveForward);
            return;
        }

        int direction = moveForward ? 1 : -1;
        _windowSwitchSelectedIndex = (_windowSwitchSelectedIndex + direction + _windowSwitchEntries.Count) % _windowSwitchEntries.Count;
        ShowAltTabSwitcher();
    }

    private void OnAltTabSwitchCommitted()
    {
        if (_windowSwitchEntries.Count == 0 || _windowSwitchSelectedIndex < 0 || _windowSwitchSelectedIndex >= _windowSwitchEntries.Count)
        {
            HideAltTabSwitcher();
            return;
        }

        IntPtr targetWindow = _windowSwitchEntries[_windowSwitchSelectedIndex].Handle;
        HideAltTabSwitcher();
        WindowSwitcherService.ActivateWindow(targetWindow);
    }

    private void OnAltTabSwitchCanceled()
    {
        HideAltTabSwitcher();
    }

    private void ShowAltTabSwitcher()
    {
        if (_altTabWindow is null || _windowSwitchEntries.Count == 0)
        {
            return;
        }

        ScreenBounds displayBounds = WindowSwitcherService.GetDisplayBoundsForCursorOrPrimary();
        _altTabWindow.ShowSwitcher(_windowSwitchEntries, _windowSwitchSelectedIndex, displayBounds);
    }

    private void HideAltTabSwitcher()
    {
        _altTabWindow?.HideSwitcher();
        _windowSwitchEntries = [];
        _windowSwitchSelectedIndex = -1;
        _altTabHookService?.ResetSession();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Unhandled WinUI exception.", e.Exception);
    }

    private void OnCurrentDomainUnhandledException(object? sender, System.UnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Unhandled AppDomain exception.", e.ExceptionObject as Exception);
    }
}
