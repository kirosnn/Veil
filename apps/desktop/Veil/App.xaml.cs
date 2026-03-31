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
    private static readonly TimeSpan TopBarActivityInterval = TimeSpan.FromMilliseconds(400);
    private readonly AppSettings _settings;
    private readonly GameDetectionService _gameDetectionService = new();
    private readonly Dictionary<string, TopBarWindow> _topBarWindows = new(StringComparer.OrdinalIgnoreCase);
    private TrayIconService? _trayIcon;
    private AltTabHookService? _altTabHookService;
    private AltTabWindow? _altTabWindow;
    private IReadOnlyList<WindowSwitchEntry> _windowSwitchEntries = [];
    private int _windowSwitchSelectedIndex = -1;
    private DispatcherTimer? _monitorRefreshTimer;
    private DispatcherTimer? _topBarActivityTimer;
    private string _lastTopologySignature = string.Empty;
    private bool _isSyncingTopBars;

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

            _trayIcon = new TrayIconService();
            _trayIcon.ShowRequested += OnTrayShowRequested;
            _trayIcon.SettingsRequested += OnTraySettingsRequested;
            _trayIcon.QuitRequested += OnTrayQuitRequested;
            _trayIcon.Initialize();

            InitializeAltTabSwitcher();
            _gameDetectionService.WarmUp();
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
        _monitorRefreshTimer?.Stop();
        _monitorRefreshTimer = null;
        _topBarActivityTimer?.Stop();
        _topBarActivityTimer = null;
        DisposeAltTabSwitcher();
        _trayIcon?.Dispose();
        _trayIcon = null;
        CloseAllTopBarWindows();
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
            Interval = TimeSpan.FromSeconds(2)
        };
        _monitorRefreshTimer.Tick += OnMonitorRefreshTick;
        _monitorRefreshTimer.Start();
    }

    private void OnMonitorRefreshTick(object? sender, object e)
    {
        SyncTopBarWindows();
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
        RefreshTopBarActivityState();
    }

    private void RefreshTopBarActivityState()
    {
        if (_topBarWindows.Count == 0)
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
            bool gameRunning = configuredGameRunning;
            int? activeGameProcessId = null;
            bool shouldHideForForegroundWindow = false;

            if (hasForegroundProcess)
            {
                shouldHideForForegroundWindow = GameDetectionService.IsWindowFullscreenLike(foregroundProcessInfo.WindowRect, topBarWindow.ScreenBounds);

                if (!gameRunning)
                {
                    activeGameProcessId = _gameDetectionService.TryGetForegroundGameProcessIdForScreen(
                        foregroundProcessInfo,
                        topBarWindow.ScreenBounds,
                        configuredProcessNames);
                    gameRunning = activeGameProcessId.HasValue;
                }
            }

            topBarWindow.ApplyActivityState(gameRunning, shouldHideForForegroundWindow, activeGameProcessId);
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
                CloseAllTopBarWindows();
                _lastTopologySignature = string.Empty;
                return;
            }

            string topologySignature = string.Join(
                "|",
                monitors.Select(static monitor =>
                    $"{monitor.Id}:{monitor.Bounds.Left},{monitor.Bounds.Top},{monitor.Bounds.Right},{monitor.Bounds.Bottom}:{monitor.IsPrimary}"));
            bool topologyChanged = force || !string.Equals(_lastTopologySignature, topologySignature, StringComparison.Ordinal);

            IReadOnlyList<string> enabledMonitorIds = _settings.ResolveTopBarMonitorIds(monitors);
            string hotkeyOwnerId = monitors
                .FirstOrDefault(monitor => monitor.IsPrimary && enabledMonitorIds.Contains(monitor.Id, StringComparer.OrdinalIgnoreCase))?.Id
                ?? enabledMonitorIds[0];

            if (topologyChanged)
            {
                CloseAllTopBarWindows();
                _lastTopologySignature = topologySignature;
            }

            foreach (string existingMonitorId in _topBarWindows.Keys.ToArray())
            {
                if (enabledMonitorIds.Contains(existingMonitorId, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                _topBarWindows[existingMonitorId].Close();
                _topBarWindows.Remove(existingMonitorId);
            }

            foreach (MonitorInfo2 monitor in monitors.Where(monitor => enabledMonitorIds.Contains(monitor.Id, StringComparer.OrdinalIgnoreCase)))
            {
                bool ownsGlobalHotkeys = string.Equals(monitor.Id, hotkeyOwnerId, StringComparison.OrdinalIgnoreCase);

                if (!_topBarWindows.TryGetValue(monitor.Id, out TopBarWindow? topBarWindow))
                {
                    topBarWindow = new TopBarWindow(monitor.Id, monitor.ToScreenBounds(), ownsGlobalHotkeys);
                    _topBarWindows[monitor.Id] = topBarWindow;
                    topBarWindow.Activate();
                    AppLogger.Info($"TopBarWindow activated for {monitor.Id}.");
                    continue;
                }

                topBarWindow.SetOwnsGlobalHotkeys(ownsGlobalHotkeys);
            }
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
    {
        foreach (TopBarWindow topBarWindow in _topBarWindows.Values.ToArray())
        {
            topBarWindow.Close();
        }

        _topBarWindows.Clear();
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
