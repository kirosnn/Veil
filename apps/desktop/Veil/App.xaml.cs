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
    private readonly AppSettings _settings;
    private readonly VeilOptimizationService _veilOptimizationService = new();
    private readonly Dictionary<string, TopBarWindow> _topBarWindows = new(StringComparer.OrdinalIgnoreCase);
    private DesktopIconVisibilityService? _desktopIconVisibilityService;
    private DesktopContextMenuService? _desktopContextMenu;
    private TrayIconService? _trayIcon;
    private AltTabHookService? _altTabHookService;
    private AltTabWindow? _altTabWindow;
    private IReadOnlyList<WindowSwitchEntry> _windowSwitchEntries = [];
    private int _windowSwitchSelectedIndex = -1;
    private DispatcherTimer? _monitorRefreshTimer;
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
            EnsureWorkingDirectory();
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
            _ = InstalledAppService.PreloadAsync();
            SyncTopBarWindows(true);
            StartMonitorRefresh();

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

    private static void EnsureWorkingDirectory()
    {
        try
        {
            string baseDirectory = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory))
            {
                return;
            }

            Environment.CurrentDirectory = baseDirectory;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to set process working directory.", ex);
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
        DisposeAltTabSwitcher();
        _desktopContextMenu?.Dispose();
        _desktopContextMenu = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        CloseAllTopBarWindows();
        _veilOptimizationService.RestoreNormalOptimizations();
        _veilOptimizationService.Dispose();
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

            HashSet<string> enabledMonitorIdSet = enabledMonitorIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            string hotkeyOwnerId = monitors
                .FirstOrDefault(monitor => monitor.IsPrimary && enabledMonitorIdSet.Contains(monitor.Id))?.Id
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
                if (enabledMonitorIdSet.Contains(existingMonitorId))
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

            foreach (MonitorInfo2 monitor in monitors.Where(monitor => enabledMonitorIdSet.Contains(monitor.Id)))
            {
                bool ownsGlobalHotkeys = string.Equals(monitor.Id, hotkeyOwnerId, StringComparison.OrdinalIgnoreCase);

                if (!_topBarWindows.TryGetValue(monitor.Id, out TopBarWindow? topBarWindow))
                {
                    try
                    {
                        TopBarWindow createdWindow = new(
                            monitor.Id,
                            monitor.ToScreenBounds(),
                            _veilOptimizationService,
                            ownsGlobalHotkeys,
                            _startTopBarsHiddenUntilReady);
                        createdWindow.PrepareForActivation();
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
        e.Handled = true;
    }

    private void OnCurrentDomainUnhandledException(object? sender, System.UnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Unhandled AppDomain exception.", e.ExceptionObject as Exception);
    }
}
