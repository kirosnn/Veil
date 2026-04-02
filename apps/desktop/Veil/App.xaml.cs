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
    private const string OpenWidgetEventName = @"Local\Veil.OpenWidget";
    private readonly AppSettings _settings;
    private readonly GameDetectionService _gameDetectionService = new();
    private readonly Dictionary<string, TopBarWindow> _topBarWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DockWindow> _dockWindows = new(StringComparer.OrdinalIgnoreCase);
    private DesktopShellService? _desktopShellService;
    private DesktopWidgetManager? _desktopWidgetManager;
    private DesktopContextMenuService? _desktopContextMenu;
    private TrayIconService? _trayIcon;
    private AltTabHookService? _altTabHookService;
    private AltTabWindow? _altTabWindow;
    private IReadOnlyList<WindowSwitchEntry> _windowSwitchEntries = [];
    private int _windowSwitchSelectedIndex = -1;
    private DispatcherTimer? _monitorRefreshTimer;
    private DispatcherTimer? _topBarActivityTimer;
    private WidgetWindow? _widgetWindow;
    private EventWaitHandle? _openWidgetEvent;
    private RegisteredWaitHandle? _openWidgetRegistration;
    private string _lastTopologySignature = string.Empty;
    private bool _isSyncingTopBars;
    private bool _openWidgetOnStartup;
    private int _emptyMonitorRefreshCount;

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
            _openWidgetOnStartup = IsWidgetLaunchRequested(args.Arguments);
            if (_openWidgetOnStartup && TrySignalRunningInstanceToOpenWidget())
            {
                Exit();
                return;
            }

            KillOtherInstances();
            PerformanceLogger.Start();
            AppLogger.Info("Application launch started.");
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

            _desktopShellService = new DesktopShellService(new WindowsDesktopShellBridge());
            if (!_desktopShellService.TryApplyLaunchState())
            {
                AppLogger.Error("Desktop shell launch state could not be fully applied.");
            }

            _trayIcon = new TrayIconService();
            _trayIcon.ShowRequested += OnTrayShowRequested;
            _trayIcon.SettingsRequested += OnTraySettingsRequested;
            _trayIcon.QuitRequested += OnTrayQuitRequested;
            _trayIcon.Initialize();
            _desktopWidgetManager = new DesktopWidgetManager(_settings);
            _ = _desktopWidgetManager.InitializeAsync();

            InitializeDesktopContextMenu();
            InitializeAltTabSwitcher();
            _gameDetectionService.WarmUp();
            SyncTopBarWindows(true);
            SyncDockWindows(true);
            InitializeWidgetLaunchBridge();
            StartMonitorRefresh();
            StartTopBarActivityRefresh();

            if (_openWidgetOnStartup)
            {
                OpenWidgetWindow();
            }
            else if (_settings.IsFirstLaunch)
            {
                GetPreferredTopBarWindow()?.OpenSettings();
            }
        }
        catch (Exception ex)
        {
            try
            {
                _desktopShellService?.RestoreLaunchState();
            }
            catch (Exception restoreEx)
            {
                AppLogger.Error("Failed to restore desktop shell state after launch error.", restoreEx);
            }

            AppLogger.Error("Fatal error during launch.", ex);
            throw;
        }
    }

    private static void KillOtherInstances()
    {
        int currentPid = Environment.ProcessId;
        string currentName = Process.GetCurrentProcess().ProcessName;

        foreach (var process in Process.GetProcessesByName(currentName))
        {
            try
            {
                if (process.Id != currentPid)
                {
                    AppLogger.Info($"Killing previous instance (pid {process.Id}).");
                    process.Kill();
                    process.WaitForExit(3000);
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
    }

    private static bool IsWidgetLaunchRequested(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        return arguments
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(argument => string.Equals(argument, "--widget", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TrySignalRunningInstanceToOpenWidget()
    {
        try
        {
            using EventWaitHandle openWidgetEvent = EventWaitHandle.OpenExisting(OpenWidgetEventName);
            return openWidgetEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    private void InitializeWidgetLaunchBridge()
    {
        var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dispatcherQueue is null)
        {
            return;
        }

        _openWidgetEvent = new EventWaitHandle(false, EventResetMode.AutoReset, OpenWidgetEventName);
        _openWidgetRegistration = ThreadPool.RegisterWaitForSingleObject(
            _openWidgetEvent,
            (_, _) => dispatcherQueue.TryEnqueue(OpenWidgetWindow),
            null,
            Timeout.Infinite,
            false);
    }

    private void OpenWidgetWindow()
    {
        if (_widgetWindow is not null)
        {
            _widgetWindow.ShowCentered();
            return;
        }

        ScreenBounds screen = GetPreferredScreenBounds();
        string preferredMonitorId = GetPreferredMonitorId();
        _widgetWindow = new WidgetWindow(screen, preferredMonitorId);
        _widgetWindow.Closed += OnWidgetWindowClosed;
        _widgetWindow.ShowCentered();
    }

    private void OnWidgetWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is WidgetWindow widgetWindow)
        {
            widgetWindow.Closed -= OnWidgetWindowClosed;
        }

        _widgetWindow = null;
    }

    private string GetPreferredMonitorId()
    {
        if (_topBarWindows.Count > 0)
        {
            List<MonitorInfo2> monitors = MonitorService.GetAllMonitors();
            IReadOnlyList<string> enabledMonitorIds = _settings.ResolveTopBarMonitorIds(monitors);
            string? preferredMonitorId = monitors
                .FirstOrDefault(monitor => monitor.IsPrimary && enabledMonitorIds.Contains(monitor.Id, StringComparer.OrdinalIgnoreCase))?.Id
                ?? enabledMonitorIds.FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(preferredMonitorId))
            {
                return preferredMonitorId;
            }
        }

        return MonitorService.GetAllMonitors().FirstOrDefault(static monitor => monitor.IsPrimary)?.Id
            ?? string.Empty;
    }

    private ScreenBounds GetPreferredScreenBounds()
    {
        TopBarWindow? preferredTopBarWindow = GetPreferredTopBarWindow();
        if (preferredTopBarWindow is not null)
        {
            return preferredTopBarWindow.ScreenBounds;
        }

        MonitorInfo2? preferredMonitor = MonitorService.GetAllMonitors().FirstOrDefault(static monitor => monitor.IsPrimary)
            ?? MonitorService.GetAllMonitors().FirstOrDefault();

        return preferredMonitor?.ToScreenBounds() ?? new ScreenBounds(160, 160, 1440, 960);
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
        _openWidgetRegistration?.Unregister(null);
        _openWidgetRegistration = null;
        _openWidgetEvent?.Dispose();
        _openWidgetEvent = null;
        if (_widgetWindow is not null)
        {
            _widgetWindow.Closed -= OnWidgetWindowClosed;
            _widgetWindow.Close();
            _widgetWindow = null;
        }
        DisposeAltTabSwitcher();
        _desktopContextMenu?.Dispose();
        _desktopContextMenu = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _desktopWidgetManager?.Dispose();
        _desktopWidgetManager = null;
        CloseAllDockWindows();
        CloseAllTopBarWindows();
        _desktopShellService?.RestoreLaunchState();
        _desktopShellService = null;
        PerformanceLogger.Stop();
    }

    private void OnSettingsChanged()
    {
        SyncTopBarWindows();
        SyncDockWindows();
        _desktopWidgetManager?.SyncWindows();
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
            SyncDockWindows();
            _desktopWidgetManager?.SyncWindows();
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
                        TopBarWindow createdWindow = new(monitor.Id, monitor.ToScreenBounds(), ownsGlobalHotkeys);
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
                    topBarWindow.SetOwnsGlobalHotkeys(ownsGlobalHotkeys);
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Failed to refresh top bar ownership for {monitor.Id}.", ex);
                }
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

    private void SyncDockWindows(bool force = false)
    {
        try
        {
            List<MonitorInfo2> monitors = MonitorService.GetAllMonitors();
            if (monitors.Count == 0)
            {
                CloseAllDockWindows("monitor-enumeration-empty");
                return;
            }

            string topologySignature = string.Join(
                "|",
                monitors.Select(static monitor =>
                    $"{monitor.Id}:{monitor.Bounds.Left},{monitor.Bounds.Top},{monitor.Bounds.Right},{monitor.Bounds.Bottom}:{monitor.IsPrimary}"));
            IReadOnlyList<string> enabledMonitorIds = _settings.ResolveTopBarMonitorIds(monitors);

            if (enabledMonitorIds.Count == 0)
            {
                CloseAllDockWindows("monitor-resolution-empty");
                return;
            }

            if (force)
            {
                CloseAllDockWindows("force-sync");
            }

            foreach (string existingMonitorId in _dockWindows.Keys.ToArray())
            {
                if (enabledMonitorIds.Contains(existingMonitorId, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                AppLogger.Info($"Closing dock for disabled monitor {existingMonitorId}.");
                try
                {
                    _dockWindows[existingMonitorId].Close();
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Failed to close dock for disabled monitor {existingMonitorId}.", ex);
                }
                finally
                {
                    _dockWindows.Remove(existingMonitorId);
                }
            }

            foreach (MonitorInfo2 monitor in monitors.Where(monitor => enabledMonitorIds.Contains(monitor.Id, StringComparer.OrdinalIgnoreCase)))
            {
                if (_dockWindows.ContainsKey(monitor.Id))
                {
                    continue;
                }

                try
                {
                    DockWindow createdWindow = new(monitor.Id, monitor.ToScreenBounds());
                    createdWindow.Activate();
                    _dockWindows[monitor.Id] = createdWindow;
                    AppLogger.Info($"DockWindow activated for {monitor.Id} on topology {topologySignature}.");
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Failed to create dock for {monitor.Id}.", ex);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to sync dock windows.", ex);
        }
    }

    private void CloseAllDockWindows()
        => CloseAllDockWindows("unspecified");

    private void CloseAllDockWindows(string reason)
    {
        if (_dockWindows.Count == 0)
        {
            return;
        }

        AppLogger.Info($"Closing all dock windows. Reason: {reason}.");
        foreach (DockWindow dockWindow in _dockWindows.Values.ToArray())
        {
            try
            {
                dockWindow.Close();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to close a dock window.", ex);
            }
        }

        _dockWindows.Clear();
    }

    private void InitializeDesktopContextMenu()
    {
        var dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dispatcherQueue is null)
        {
            return;
        }

        _desktopContextMenu = new DesktopContextMenuService(dispatcherQueue);
        _desktopContextMenu.AddWidgetRequested += OnDesktopAddWidget;
        _desktopContextMenu.WidgetSettingsRequested += OpenWidgetWindow;
        _desktopContextMenu.SettingsRequested += OnTraySettingsRequested;
        _desktopContextMenu.Initialize();
    }

    private void OnDesktopAddWidget(string kind)
    {
        string monitorId = GetPreferredMonitorId();
        _settings.AddDesktopWidget(kind, monitorId);
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
