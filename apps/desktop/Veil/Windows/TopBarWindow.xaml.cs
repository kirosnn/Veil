using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Veil.Configuration;
using Veil.Diagnostics;
using Veil.Interop;
using Veil.Services;
using WinRT;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class TopBarWindow : Window
{
    private const int BarHeight = 32;
    private const double MinimumClockClearance = 164;
    private const double DefaultRenderedTopBarOpacity = 0.92;
    private const double MinimumRenderedTopBarOpacity = 0.04;
    private const double DefaultRenderedBlurIntensity = 0.55;
    private const double MinimumRenderedBlurIntensity = 0.20;
    private static readonly TimeSpan AdaptiveVisualWarmRefreshInterval = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan AdaptiveVisualRefreshInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan AdaptiveVisualHotHoldDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan AdaptiveVisualWarmHoldDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DiscordHotHoldDuration = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan DiscordWarmHoldDuration = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BackgroundMaintenanceInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BackgroundMaintenanceWarmInterval = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan BackgroundMaintenanceHotHoldDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BackgroundMaintenanceWarmHoldDuration = TimeSpan.FromMinutes(4);
    private static readonly ImageSource YouTubeLightIconSource = new SvgImageSource(new Uri("ms-appx:///Assets/Icons/youtube.svg"));
    private static readonly ImageSource YouTubeDarkIconSource = new SvgImageSource(new Uri("ms-appx:///Assets/Icons/youtube-dark.svg"));
    private static readonly ImageSource DiscordLightIconSource = new SvgImageSource(new Uri("ms-appx:///Assets/Icons/discord.svg"));
    private static readonly ImageSource DiscordDarkIconSource = new SvgImageSource(new Uri("ms-appx:///Assets/Icons/discord-dark.svg"));
    private static readonly ImageSource PanelThemeLightIconSource = new SvgImageSource(new Uri("ms-appx:///Assets/Icons/theme-adjustments.svg"));
    private static readonly ImageSource PanelThemeDarkIconSource = new SvgImageSource(new Uri("ms-appx:///Assets/Icons/theme-adjustments-dark.svg"));
    private static readonly DiscordNotificationService SharedDiscordNotificationService = new();
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _visualTimer;
    private readonly DemandDrivenModule _adaptiveVisualModule;
    private readonly DemandDrivenModule _discordModule;
    private readonly DemandDrivenModule _backgroundMaintenanceModule;
    private readonly string _monitorId;
    private readonly ScreenBounds _screen;
    private readonly AppSettings _settings;
    private readonly string _discordDemandOwnerId;
    private bool _ownsGlobalHotkeys;
    private bool _isHidden;
    private bool _isGameMinimalMode;
    private bool _appBarRegistered;
    private IntPtr _hwnd;
    private global::Windows.UI.Color? _lastAdaptiveColor;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private FinderWindow? _finderWindow;
    private FinderHotkeyService? _finderHotkeyService;
    private SettingsWindow? _settingsWindow;
    private SystemStatsWindow? _systemStatsWindow;
    private WeatherWindow? _weatherWindow;
    private PanelThemeWindow? _panelThemeWindow;
    private MusicControlWindow? _musicControlWindow;
    private DiscordNotificationWindow? _discordNotificationWindow;
    private readonly DiscordNotificationService _discordNotificationService;
    private readonly MediaControlService _mediaControlService = new();
    private readonly WeatherService _weatherService = new();
    private RunCatService? _runCatService;
    private readonly GamePerformanceService _gamePerformanceService = new();
    private ImageSource?[]? _runCatFrames;
    private int _runCatLoadVersion;
    private int _musicAlbumArtLoadVersion;
    private string? _lastRunCatTintHex;
    private readonly DispatcherTimer _weatherRefreshTimer;
    private readonly DispatcherTimer _backgroundMaintenanceTimer;
    private int _backgroundMaintenanceInFlight;
    private string _lastWindowRegionSignature = string.Empty;
    private string _lastClockText = string.Empty;
    private DateTime _lastAdaptiveVisualBoostUtc = DateTime.MinValue;
    private DateTime _lastDiscordBoostUtc = DateTime.MinValue;
    private DateTime _lastBackgroundMaintenanceBoostUtc = DateTime.MinValue;

    private readonly record struct WindowRegionSegment(int Left, int Top, int Right, int Bottom);

    internal TopBarWindow(string monitorId, ScreenBounds screen, bool ownsGlobalHotkeys)
    {
        _monitorId = monitorId;
        _screen = screen;
        _settings = AppSettings.Current;
        _ownsGlobalHotkeys = ownsGlobalHotkeys;
        _discordDemandOwnerId = $"topbar:{monitorId}";
        _discordNotificationService = SharedDiscordNotificationService;

        InitializeComponent();
        Title = "Veil TopBar";
        UpdateWeatherButton();

        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += OnClockTick;
        _clockTimer.Start();
        UpdateClock();

        _visualTimer = new DispatcherTimer
        {
            Interval = AdaptiveVisualRefreshInterval
        };
        _visualTimer.Tick += OnVisualTick;

        _weatherRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(20)
        };
        _weatherRefreshTimer.Tick += OnWeatherRefreshTick;

        _backgroundMaintenanceTimer = new DispatcherTimer
        {
            Interval = BackgroundMaintenanceInterval
        };
        _backgroundMaintenanceTimer.Tick += OnBackgroundMaintenanceTick;

        _adaptiveVisualModule = new DemandDrivenModule(
            AdaptiveVisualWarmHoldDuration,
            AdaptiveVisualHotHoldDuration,
            OnAdaptiveVisualTemperatureChanged);
        _discordModule = new DemandDrivenModule(
            DiscordWarmHoldDuration,
            DiscordHotHoldDuration,
            OnDiscordTemperatureChanged);
        _backgroundMaintenanceModule = new DemandDrivenModule(
            BackgroundMaintenanceWarmHoldDuration,
            BackgroundMaintenanceHotHoldDuration,
            OnBackgroundMaintenanceTemperatureChanged);

        Activated += OnActivated;
        Closed += OnClosed;
        _settings.Changed += OnSettingsChanged;
        SizeChanged += OnTopBarSizeChanged;
        LeftButtonsPanel.SizeChanged += OnLeftButtonsPanelSizeChanged;
        RightButtonsPanel.SizeChanged += OnRightButtonsPanelSizeChanged;
        ApplySettings();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnActivated;

        _hwnd = WindowHelper.GetHwnd(this);

        WindowHelper.RemoveTitleBar(this);
        WindowHelper.ExtendFrameIntoClientArea(this);
        WindowHelper.MakeOverlay(this);

        int width = _screen.Right - _screen.Left;
        WindowHelper.PositionOnMonitor(this, _screen.Left, _screen.Top, width, BarHeight);

        WindowHelper.SetAlwaysOnTop(this);
        WindowHelper.RegisterAppBar(this, _screen, ABE_TOP, BarHeight);
        _appBarRegistered = true;
        ApplySettings();

        RebuildShortcutButtons();
        _ = InstalledAppService.PreloadAsync();
        _ = InitWeatherAsync();
        _ = ApplyRunCatSettingsAsync();
        _ = InitMediaControlAsync();
        _ = InitDiscordNotificationsAsync();
        ApplyHotkeyOwnership();
        UpdateVisualRefreshState(boost: true);
        UpdateDiscordDemand(boost: true);
        UpdateBackgroundMaintenanceState(boost: true);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _clockTimer.Stop();
        _visualTimer.Stop();
        _weatherRefreshTimer.Stop();
        _backgroundMaintenanceTimer.Stop();
        _settings.Changed -= OnSettingsChanged;
        _weatherService.StateChanged -= OnWeatherStateChanged;
        _mediaControlService.StateChanged -= OnMediaStateChanged;
        _discordNotificationService.NotificationsChanged -= OnDiscordNotificationsChanged;
        _discordNotificationService.ReleaseDemand(_discordDemandOwnerId);
        SizeChanged -= OnTopBarSizeChanged;
        LeftButtonsPanel.SizeChanged -= OnLeftButtonsPanelSizeChanged;
        RightButtonsPanel.SizeChanged -= OnRightButtonsPanelSizeChanged;
        _acrylicController?.Dispose();
        _finderWindow?.Destroy();
        _finderWindow = null;
        DisposeFinderHotkey();
        _runCatService?.Dispose();
        _gamePerformanceService.RestoreNormalOptimizations();
        _gamePerformanceService.Dispose();
        _runCatService = null;
        if (_appBarRegistered)
        {
            WindowHelper.UnregisterAppBar(this);
        }
    }

    internal ScreenBounds ScreenBounds => _screen;

    internal IntPtr WindowHandle => _hwnd;

    internal bool IsMinimalModeActive => _isGameMinimalMode;

    internal void ApplyActivityState(bool gameRunning, bool shouldHideForForegroundWindow, int? activeGameProcessId)
    {
        bool visibilityChanged = false;
        if (gameRunning)
        {
            EnterMinimalMode(activeGameProcessId);
            return;
        }

        if (_isGameMinimalMode)
        {
            ExitMinimalMode();
        }

        bool shouldHideWindow = shouldHideForForegroundWindow;

        if (shouldHideWindow && !_isHidden)
        {
            _isHidden = true;
            visibilityChanged = true;
            if (_appBarRegistered)
            {
                WindowHelper.UnregisterAppBar(this);
                _appBarRegistered = false;
            }
            WindowHelper.HideWindow(this);
        }
        else if (!shouldHideWindow && _isHidden)
        {
            _isHidden = false;
            visibilityChanged = true;
            WindowHelper.ShowWindow(this);

            int width = _screen.Right - _screen.Left;
            WindowHelper.PositionOnMonitor(this, _screen.Left, _screen.Top, width, BarHeight);
            WindowHelper.SetAlwaysOnTop(this);
            WindowHelper.RegisterAppBar(this, _screen, ABE_TOP, BarHeight);
            _appBarRegistered = true;
        }

        if (visibilityChanged)
        {
            UpdateVisualRefreshState(boost: !_isHidden);
            UpdateDiscordDemand(boost: !_isHidden);
            UpdateBackgroundMaintenanceState(boost: !_isHidden);
        }
    }

    private void OnBackgroundMaintenanceTick(object? sender, object e)
    {
        UpdateBackgroundMaintenanceState();
        QueueBackgroundMaintenance();
    }

    private void OnMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isGameMinimalMode)
        {
            return;
        }

        OpenSettingsWindow();
    }

    internal void OpenSettings() => OpenSettingsWindow();

    private void OpenSettingsWindow()
    {
        try
        {
            AppLogger.Info("Settings window open requested.");
            if (_settingsWindow != null)
            {
                _settingsWindow.Close();
            }

            _settingsWindow = new SettingsWindow(_screen, _monitorId);
            _settingsWindow.Closed += OnSettingsWindowClosed;
            _settingsWindow.ShowCentered();
            AppLogger.Info("Settings window show requested.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to open settings window.", ex);
        }
    }

    private void OnFinderButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isGameMinimalMode)
        {
            return;
        }

        OpenFinderWindow();
    }

    private void InitializeFinderHotkey()
    {
        if (_finderHotkeyService is not null)
        {
            return;
        }

        _finderHotkeyService = new FinderHotkeyService();
        _finderHotkeyService.Triggered += OnFinderHotkeyTriggered;
        _finderHotkeyService.Initialize();
        _finderHotkeyService.SetEnabled(_settings.FinderHotkeyEnabled);
    }

    private void DisposeFinderHotkey()
    {
        if (_finderHotkeyService is null)
        {
            return;
        }

        _finderHotkeyService.Triggered -= OnFinderHotkeyTriggered;
        _finderHotkeyService.Dispose();
        _finderHotkeyService = null;
    }

    private void ApplyHotkeyOwnership()
    {
        if (_ownsGlobalHotkeys)
        {
            InitializeFinderHotkey();
            _finderHotkeyService?.SetEnabled(_settings.FinderHotkeyEnabled);
            return;
        }

        DisposeFinderHotkey();
    }

    internal void SetOwnsGlobalHotkeys(bool ownsGlobalHotkeys)
    {
        if (_ownsGlobalHotkeys == ownsGlobalHotkeys)
        {
            return;
        }

        _ownsGlobalHotkeys = ownsGlobalHotkeys;
        ApplyHotkeyOwnership();
    }

    private void OnFinderHotkeyTriggered()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isGameMinimalMode)
            {
                return;
            }

            OpenFinderWindow();
        });
    }

    private void EnsureFinderWindowCreated()
    {
        if (_finderWindow != null) return;

        _finderWindow = new FinderWindow(_screen);
    }

    private void OpenFinderWindow()
    {
        try
        {
            EnsureFinderWindowCreated();
            _finderWindow!.ShowCentered();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to open finder window.", ex);
        }
    }

    private void OnRunCatButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isGameMinimalMode)
        {
            return;
        }

        if (_systemStatsWindow is { IsStatsVisible: true })
        {
            _systemStatsWindow.Hide();
            return;
        }

        if (_systemStatsWindow is null)
        {
            _systemStatsWindow = new SystemStatsWindow();
            _systemStatsWindow.Initialize();
        }

        if ((DateTime.UtcNow - _systemStatsWindow.LastHiddenAtUtc).TotalMilliseconds < 200)
        {
            return;
        }

        int barWidth = _screen.Right - _screen.Left;
        _systemStatsWindow.ShowAt(_screen.Left + barWidth - 6, _screen.Top + BarHeight);
    }

    private void OnPanelThemeButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isGameMinimalMode)
        {
            return;
        }

        if (_panelThemeWindow is { IsPanelVisible: true })
        {
            _panelThemeWindow.Hide();
            return;
        }

        if (_panelThemeWindow is null)
        {
            _panelThemeWindow = new PanelThemeWindow();
            _panelThemeWindow.Initialize();
        }

        if ((DateTime.UtcNow - _panelThemeWindow.LastHiddenAtUtc).TotalMilliseconds < 200)
        {
            return;
        }

        int barWidth = _screen.Right - _screen.Left;
        _panelThemeWindow.ShowAt(_screen.Left + barWidth - 6, _screen.Top + BarHeight);
    }

    private async Task InitWeatherAsync()
    {
        try
        {
            _weatherService.StateChanged -= OnWeatherStateChanged;
            _weatherService.StateChanged += OnWeatherStateChanged;
            await _weatherService.InitializeAsync();
            DispatcherQueue.TryEnqueue(UpdateWeatherButton);
            _weatherRefreshTimer.Start();
        }
        catch
        {
        }
    }

    private void OnWeatherRefreshTick(object? sender, object e)
    {
        _ = _weatherService.RefreshAsync(true);
    }

    private void OnWeatherStateChanged()
    {
        DispatcherQueue.TryEnqueue(UpdateWeatherButton);
    }

    private void UpdateWeatherButton()
    {
        WeatherButton.Visibility = _settings.WeatherButtonEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!_settings.WeatherButtonEnabled && _weatherWindow is { IsWeatherVisible: true })
        {
            _weatherWindow.Hide();
        }

        var snapshot = _weatherService.Snapshot;
        string text = snapshot is null
            ? "--°"
            : $"{Math.Round(snapshot.PrimaryCity.TemperatureC):0}°";
        WeatherButtonText.Text = text;

        WeatherIconHost.Children.Clear();
        WeatherIconHost.Children.Add(WeatherVisualFactory.CreateIcon(
            snapshot?.PrimaryCity.WeatherCode ?? 0,
            snapshot?.PrimaryCity.IsDay ?? true,
            14,
            UseDarkTopBarForeground(GetTopBarForegroundColor())));
    }

    private void OnWeatherButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isGameMinimalMode)
        {
            return;
        }

        if (_weatherWindow is { IsWeatherVisible: true })
        {
            _weatherWindow.Hide();
            return;
        }

        if (_weatherWindow is null)
        {
            _weatherWindow = new WeatherWindow();
            _weatherWindow.SetWeatherService(_weatherService);
            _weatherWindow.Initialize();
        }

        if ((DateTime.UtcNow - _weatherWindow.LastHiddenAtUtc).TotalMilliseconds < 200)
        {
            return;
        }

        int barWidth = _screen.Right - _screen.Left;
        _weatherWindow.ShowAt(_screen.Left + barWidth - 6, _screen.Top + BarHeight);
    }

    private async Task InitMediaControlAsync()
    {
        try
        {
            await _mediaControlService.InitializeAsync();
            _mediaControlService.StateChanged += OnMediaStateChanged;
            DispatcherQueue.TryEnqueue(UpdateMusicButtonVisibility);
        }
        catch
        {
        }
    }

    private void OnMediaStateChanged()
    {
        DispatcherQueue.TryEnqueue(UpdateMusicButtonVisibility);
    }

    private void UpdateMusicButtonVisibility()
    {
        bool isVisible = _settings.MusicButtonEnabled && _mediaControlService.IsMusicApp;
        MusicButton.Visibility = isVisible
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!isVisible && _musicControlWindow is { IsMenuVisible: true })
        {
            _musicControlWindow.Hide();
        }

        _ = UpdateMusicAlbumArtAsync();
    }

    private async Task UpdateMusicAlbumArtAsync()
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        int loadVersion = ++_musicAlbumArtLoadVersion;

        try
        {
            if (_mediaControlService.IsYouTubeSource)
            {
                if (loadVersion != _musicAlbumArtLoadVersion)
                {
                    return;
                }

                MusicAlbumArt.Source = UseDarkTopBarForeground(GetTopBarForegroundColor())
                    ? YouTubeDarkIconSource
                    : YouTubeLightIconSource;
                MusicAlbumArt.Visibility = Visibility.Visible;
                MusicIcon.Visibility = Visibility.Collapsed;
                MusicButton.Resources["ButtonBackgroundPointerOver"] =
                    new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
                MusicButton.Resources["ButtonBackgroundPressed"] =
                    new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
                return;
            }

            try
            {
                var imageSource = await MediaArtworkLoader.LoadAsync(
                    _mediaControlService.ThumbnailRef,
                    _mediaControlService.SourceAppId,
                    _mediaControlService.TrackTitle,
                    _mediaControlService.TrackArtist,
                    _mediaControlService.AlbumTitle,
                    _mediaControlService.Subtitle);
                if (loadVersion != _musicAlbumArtLoadVersion)
                {
                    return;
                }

                if (imageSource is not null)
                {
                    MusicAlbumArt.Source = imageSource;
                    MusicAlbumArt.Visibility = Visibility.Visible;
                    MusicIcon.Visibility = Visibility.Collapsed;
                    MusicButton.Resources["ButtonBackgroundPointerOver"] =
                        new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
                    MusicButton.Resources["ButtonBackgroundPressed"] =
                        new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
                    return;
                }
            }
            catch
            {
            }

            if (loadVersion != _musicAlbumArtLoadVersion)
            {
                return;
            }

            MusicAlbumArt.Source = null;
            MusicAlbumArt.Visibility = Visibility.Collapsed;
            MusicIcon.Visibility = Visibility.Visible;
            var foregroundColor = GetTopBarForegroundColor();
            MusicButton.Resources["ButtonBackgroundPointerOver"] =
                new SolidColorBrush(global::Windows.UI.Color.FromArgb(12, foregroundColor.R, foregroundColor.G, foregroundColor.B));
            MusicButton.Resources["ButtonBackgroundPressed"] =
                new SolidColorBrush(global::Windows.UI.Color.FromArgb(8, foregroundColor.R, foregroundColor.G, foregroundColor.B));
        }
        finally
        {
            double elapsedMs = (Stopwatch.GetTimestamp() - startTimestamp) * 1000d / Stopwatch.Frequency;
            PerformanceLogger.RecordMilliseconds("TopBar.UpdateMusicAlbumArtAsync", elapsedMs, 8.0);
        }
    }

    private void OnMusicButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isGameMinimalMode)
        {
            return;
        }

        if (_musicControlWindow is { IsMenuVisible: true })
        {
            _musicControlWindow.Hide();
            return;
        }

        if (_musicControlWindow is null)
        {
            _musicControlWindow = new MusicControlWindow();
            _musicControlWindow.SetMediaService(_mediaControlService);
            _musicControlWindow.Initialize();
        }

        if ((DateTime.UtcNow - _musicControlWindow.LastHiddenAtUtc).TotalMilliseconds < 200)
        {
            return;
        }

        int barWidth = _screen.Right - _screen.Left;
        _musicControlWindow.ShowAt(_screen.Left + barWidth - 6, _screen.Top + BarHeight);
    }

    private async Task InitDiscordNotificationsAsync()
    {
        try
        {
            await _discordNotificationService.EnsureInitializedAsync();
            _discordNotificationService.NotificationsChanged += OnDiscordNotificationsChanged;
            UpdateDiscordDemand(boost: true);
            DispatcherQueue.TryEnqueue(UpdateDiscordButtonVisibility);
        }
        catch
        {
        }
    }

    private void OnDiscordNotificationsChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateDiscordButtonVisibility();
            UpdateDiscordDemand(boost: true);
        });
    }

    private void UpdateDiscordButtonVisibility()
    {
        bool isDiscordRunning = _discordNotificationService.IsDiscordRunning;
        bool isVisible = _settings.DiscordButtonEnabled
            && (isDiscordRunning
                || _discordNotificationService.UnreadCount > 0
                || _discordNotificationService.HasActiveCall);
        DiscordButton.Visibility = isVisible
            ? Visibility.Visible
            : Visibility.Collapsed;

        bool hasNotifs = _discordNotificationService.UnreadCount > 0 || _discordNotificationService.HasActiveCall;
        DiscordBadge.Visibility = hasNotifs ? Visibility.Visible : Visibility.Collapsed;
        DiscordBadgeText.Visibility = Visibility.Collapsed;
        DiscordBadge.Padding = new Thickness(0);
        DiscordBadge.MinWidth = 8;
        DiscordBadge.Height = 8;

        if (_discordNotificationService.HasActiveCall)
        {
            DiscordBadge.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 87, 242, 135));
        }
        else
        {
            DiscordBadge.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 237, 66, 69));
            DiscordBadgeText.Text = _discordNotificationService.UnreadCount > 99
                ? "99+"
                : _discordNotificationService.UnreadCount.ToString();
            DiscordBadgeText.Visibility = Visibility.Visible;
            DiscordBadge.Padding = new Thickness(3, 0, 3, 0);
            DiscordBadge.MinWidth = 12;
            DiscordBadge.Height = 12;
        }

        if (!isVisible && _discordNotificationWindow is { IsMenuVisible: true })
        {
            _discordNotificationWindow.Hide();
        }
    }

    private void OnDiscordButtonClick(object sender, RoutedEventArgs e)
    {
        UpdateDiscordDemand(boost: true);

        if (_isGameMinimalMode)
        {
            return;
        }

        if (_discordNotificationWindow is { IsMenuVisible: true })
        {
            _discordNotificationWindow.Hide();
            return;
        }

        if (_discordNotificationWindow is null)
        {
            _discordNotificationWindow = new DiscordNotificationWindow();
            _discordNotificationWindow.SetDiscordService(_discordNotificationService);
            _discordNotificationWindow.Initialize();
        }

        if ((DateTime.UtcNow - _discordNotificationWindow.LastHiddenAtUtc).TotalMilliseconds < 200)
        {
            return;
        }

        int barWidth = _screen.Right - _screen.Left;
        _discordNotificationWindow.ShowAt(_screen.Left + barWidth - 6, _screen.Top + BarHeight);
    }

    private void OnSettingsWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is SettingsWindow window)
        {
            window.Closed -= OnSettingsWindowClosed;
            if (ReferenceEquals(_settingsWindow, window))
            {
                _settingsWindow = null;
            }
        }
    }

    private void OnClockTick(object? sender, object e)
    {
        using var perfScope = PerformanceLogger.Measure("TopBar.OnClockTick", 1.5);
        UpdateClock();
        UpdateVisualRefreshState();
        UpdateDiscordDemand();
        UpdateBackgroundMaintenanceState();
    }

    private void OnVisualTick(object? sender, object e)
    {
        using var perfScope = PerformanceLogger.Measure("TopBar.OnVisualTick", 2.0);
        if (_isGameMinimalMode || _isHidden)
        {
            return;
        }

        if (_settings.TopBarStyle == "Adaptive")
        {
            UpdateAdaptiveBackground(GetEffectiveTopBarOpacity());
        }
    }

    private void UpdateVisualRefreshState(bool boost = false)
    {
        if (boost)
        {
            _lastAdaptiveVisualBoostUtc = DateTime.UtcNow;
        }

        _adaptiveVisualModule.Update(EvaluateAdaptiveVisualDemand());
    }

    private void UpdateClock()
    {
        if (_isGameMinimalMode)
        {
            return;
        }

        var now = DateTime.Now;
        var text = now.ToString("dddd d MMMM  HH:mm");
        string formatted = char.ToUpper(text[0]) + text[1..];
        if (string.Equals(formatted, _lastClockText, StringComparison.Ordinal))
        {
            return;
        }

        _lastClockText = formatted;
        ClockText.Text = formatted;
        UpdateTopBarLayout();
    }

    private void OnSettingsChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplySettings();
            RebuildShortcutButtons();
            _finderHotkeyService?.SetEnabled(_settings.FinderHotkeyEnabled);
            _musicControlWindow?.RefreshLayout();
            _discordNotificationWindow?.RefreshLayout();
            _weatherWindow?.RefreshAppearance();
            _systemStatsWindow?.RefreshAppearance();
            UpdateMusicButtonVisibility();
            UpdateDiscordButtonVisibility();
            UpdateDiscordDemand(boost: true);
            UpdateWeatherButton();
            _ = _weatherService.RefreshAsync(true);
            _ = ApplyRunCatSettingsAsync();
        });
    }

    private void ApplySettings()
    {
        using var perfScope = PerformanceLogger.Measure("TopBar.ApplySettings", 4.0);
        UpdateTopBarLayout();
        double effectiveTopBarOpacity = GetEffectiveTopBarOpacity();

        switch (_settings.TopBarStyle)
        {
            case "Transparent":
                EnsureTransparentBackdrop();
                WindowHelper.DisableLayeredTransparency(this);
                RootPanel.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
                break;
            case "Blur":
                EnsureTopBarAcrylic();
                WindowHelper.DisableLayeredTransparency(this);
                RootPanel.Background = CreateBlurBackgroundBrush();
                break;
            case "Adaptive":
                RemoveTopBarAcrylic();
                WindowHelper.DisableLayeredTransparency(this);
                UpdateAdaptiveBackground(effectiveTopBarOpacity, true);
                break;
            default:
                RemoveTopBarAcrylic();
                WindowHelper.DisableLayeredTransparency(this);
                var solidColor = ParseHexColor(_settings.SolidColor);
                RootPanel.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(
                    (byte)Math.Round(effectiveTopBarOpacity * 255), solidColor.R, solidColor.G, solidColor.B));
                break;
        }

        bool showGlassPanels = _settings.TopBarStyle is "Transparent" or "Blur";
        UpdateGlassPanels(showGlassPanels);

        UpdateVisualRefreshState(boost: true);
        UpdateWindowRegion();

        double buttonOpacity = _settings.TopBarStyle == "Solid" ? 1 : 0.92;
        var foregroundColor = GetTopBarForegroundColor();
        var foregroundBrush = new SolidColorBrush(foregroundColor);
        var mutedForegroundBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(204, foregroundColor.R, foregroundColor.G, foregroundColor.B));
        var hoverBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(12, foregroundColor.R, foregroundColor.G, foregroundColor.B));
        var pressedBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(8, foregroundColor.R, foregroundColor.G, foregroundColor.B));
        var transparentBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));

        ClockText.Foreground = foregroundBrush;
        WeatherButton.Foreground = foregroundBrush;
        WeatherButtonText.Foreground = foregroundBrush;
        MusicIcon.Foreground = mutedForegroundBrush;
        FinderButton.Foreground = foregroundBrush;
        FinderButtonText.Foreground = foregroundBrush;
        DiscordIcon.Source = UseDarkTopBarForeground(foregroundColor)
            ? DiscordDarkIconSource
            : DiscordLightIconSource;
        PanelThemeIcon.Source = UseDarkTopBarForeground(foregroundColor)
            ? PanelThemeDarkIconSource
            : PanelThemeLightIconSource;

        MenuButton.Opacity = buttonOpacity;
        FinderButton.Opacity = buttonOpacity;
        WeatherButton.Opacity = buttonOpacity;
        DiscordButton.Opacity = buttonOpacity;
        MusicButton.Opacity = buttonOpacity;
        RunCatButton.Opacity = buttonOpacity;
        PanelThemeButton.Opacity = buttonOpacity;

        MenuButton.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(170, foregroundColor.R, foregroundColor.G, foregroundColor.B));
        MenuButton.Resources["ButtonBackgroundPointerOver"] = foregroundBrush;
        MenuButton.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(119, foregroundColor.R, foregroundColor.G, foregroundColor.B));

        ApplyIconButtonResources(WeatherButton, hoverBrush, pressedBrush);
        ApplyIconButtonResources(DiscordButton, hoverBrush, pressedBrush);
        ApplyIconButtonResources(MusicButton, hoverBrush, pressedBrush);
        ApplyIconButtonResources(RunCatButton, hoverBrush, pressedBrush);
        ApplyIconButtonResources(PanelThemeButton, hoverBrush, pressedBrush);

        byte bubbleAlpha = _settings.ShowFinderBubble
            ? (byte)Math.Round(_settings.FinderBubbleOpacity * 255)
            : (byte)0;
        FinderButton.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(
            bubbleAlpha, foregroundColor.R, foregroundColor.G, foregroundColor.B));
        FinderButton.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(48, foregroundColor.R, foregroundColor.G, foregroundColor.B));
        FinderButton.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(28, foregroundColor.R, foregroundColor.G, foregroundColor.B));

        FinderButton.CornerRadius = new CornerRadius(11);
        foreach (Button shortcutButton in ShortcutButtonsPanel.Children.OfType<Button>())
        {
            shortcutButton.CornerRadius = new CornerRadius(9);
        }

        UpdateBackgroundMaintenanceState(boost: true);
    }

    private void UpdateBackgroundMaintenanceState(bool boost = false)
    {
        if (boost)
        {
            _lastBackgroundMaintenanceBoostUtc = DateTime.UtcNow;
        }

        _backgroundMaintenanceModule.Update(EvaluateBackgroundMaintenanceDemand());
    }

    private void QueueBackgroundMaintenance()
    {
        if (_isGameMinimalMode || !_settings.BackgroundOptimizationEnabled)
        {
            return;
        }

        if (Interlocked.Exchange(ref _backgroundMaintenanceInFlight, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (_settings.BackgroundOptimizationEnabled && !_isGameMinimalMode)
                {
                    _gamePerformanceService.ApplyBackgroundOptimizations();
                }
                else if (!_isGameMinimalMode)
                {
                    _gamePerformanceService.RestoreNormalOptimizations();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _backgroundMaintenanceInFlight, 0);
            }
        });
    }

    private void OnTopBarSizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        UpdateTopBarLayout();
    }

    private void OnLeftButtonsPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTopBarLayout();
    }

    private void OnRightButtonsPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTopBarLayout();
    }

    private void RebuildShortcutButtons()
    {
        ShortcutButtonsPanel.Children.Clear();

        foreach (var shortcut in _settings.ShortcutButtons.Where(setting => setting is not null).Cast<AppShortcutSetting>())
        {
            ShortcutButtonsPanel.Children.Add(CreateShortcutButton(shortcut));
        }

        bool showGlass = _settings.TopBarStyle is "Transparent" or "Blur";
        UpdateGlassPanels(showGlass);
        UpdateTopBarLayout();
    }

    private Button CreateShortcutButton(AppShortcutSetting shortcut)
    {
        var button = new Button
        {
            MinWidth = 0,
            MaxWidth = 130,
            Padding = new Thickness(9, 1, 9, 1),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(
                _settings.ShowFinderBubble ? (byte)Math.Round(_settings.FinderBubbleOpacity * 215) : (byte)0,
                GetTopBarForegroundColor().R, GetTopBarForegroundColor().G, GetTopBarForegroundColor().B)),
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(230, GetTopBarForegroundColor().R, GetTopBarForegroundColor().G, GetTopBarForegroundColor().B)),
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            IsTabStop = false,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = shortcut
        };

        var foregroundColor = GetTopBarForegroundColor();
        button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(48, foregroundColor.R, foregroundColor.G, foregroundColor.B));
        button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(28, foregroundColor.R, foregroundColor.G, foregroundColor.B));
        button.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 255, 255, 255));
        button.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 255, 255, 255));
        button.Click += OnShortcutButtonClick;

        button.Content = new TextBlock
        {
            Text = shortcut.DisplayName,
            MaxWidth = 110,
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(230, foregroundColor.R, foregroundColor.G, foregroundColor.B))
        };

        return button;
    }

    private void OnShortcutButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppShortcutSetting shortcut })
        {
            return;
        }

        InstalledAppService.LaunchOrActivate(new InstalledApp(
            shortcut.AppName,
            shortcut.AppId,
            shortcut.AppName,
            shortcut.AppName));
    }

    private void UpdateTopBarLayout()
    {
        UpdateActionButtonLayout();
        UpdateClockPosition();
        UpdateWindowRegion();
    }

    private void UpdateActionButtonLayout()
    {
        int screenWidth = _screen.Right - _screen.Left;
        double leftMargin = LeftButtonsPanel.Margin.Left + LeftButtonsPanel.Margin.Right;
        double rightWidth = RightButtonsPanel.ActualWidth + RightButtonsPanel.Margin.Left + RightButtonsPanel.Margin.Right;
        double maxLeftWidth = Math.Max(0, screenWidth - rightWidth - MinimumClockClearance - leftMargin);

        LeftButtonsPanel.MaxWidth = maxLeftWidth;

        double finderMaxWidth = Math.Clamp(maxLeftWidth * 0.42, 72, 180);
        FinderButton.MaxWidth = finderMaxWidth;
        FinderButtonText.MaxWidth = Math.Max(24, finderMaxWidth - FinderButton.Padding.Left - FinderButton.Padding.Right);

        Button[] shortcutButtons = ShortcutButtonsPanel.Children.OfType<Button>().ToArray();
        if (shortcutButtons.Length == 0)
        {
            ShortcutButtonsPanel.MaxWidth = 0;
            return;
        }

        double spacing = LeftButtonsPanel.Spacing;
        double menuAndFinderWidth = MenuButton.Width + FinderButton.MaxWidth + (spacing * 2);
        double availableShortcutWidth = Math.Max(0, maxLeftWidth - menuAndFinderWidth);
        ShortcutButtonsPanel.MaxWidth = availableShortcutWidth;

        double availablePerButtonWidth = Math.Max(
            0,
            (availableShortcutWidth - (Math.Max(0, shortcutButtons.Length - 1) * ShortcutButtonsPanel.Spacing)) / shortcutButtons.Length);
        double buttonMaxWidth = Math.Min(130, availablePerButtonWidth);
        double textMaxWidth = Math.Max(0, buttonMaxWidth - 20);

        foreach (Button button in shortcutButtons)
        {
            button.MaxWidth = buttonMaxWidth;

            if (button.Content is TextBlock textBlock)
            {
                textBlock.MaxWidth = textMaxWidth;
            }
        }
    }

    private void UpdateClockPosition()
    {
        double leftWidth = LeftButtonsPanel.ActualWidth + LeftButtonsPanel.Margin.Left + LeftButtonsPanel.Margin.Right;
        double rightWidth = RightButtonsPanel.ActualWidth + RightButtonsPanel.Margin.Left + RightButtonsPanel.Margin.Right;
        double balancedOffset = ((leftWidth - rightWidth) / 2.0) * _settings.ClockBalance;
        ClockText.Margin = new Thickness(_settings.ClockOffset + balancedOffset, 0, 0, 0);
    }

    private double _lastAppliedBlurIntensity = -1;

    private void EnsureTopBarAcrylic()
    {
        double intensity = GetEffectiveBlurIntensity();
        bool needsRecreate = _acrylicController == null
            || Math.Abs(_lastAppliedBlurIntensity - intensity) > 0.005;

        if (!needsRecreate)
        {
            return;
        }

        _lastAppliedBlurIntensity = intensity;

        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfig = null;

        float tintOpacity = (float)(intensity * 0.18);
        float luminosity = (float)Math.Min(intensity * 0.85, 0.90);
        byte tintAlpha = (byte)(intensity * 50);

        _acrylicController = new DesktopAcrylicController
        {
            TintColor = global::Windows.UI.Color.FromArgb(tintAlpha, 245, 245, 250),
            TintOpacity = tintOpacity,
            LuminosityOpacity = luminosity,
            FallbackColor = global::Windows.UI.Color.FromArgb(20, 240, 240, 245)
        };

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark
        };

        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
    }

    private void RemoveTopBarAcrylic()
    {
        _lastAppliedBlurIntensity = -1;
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfig = null;

        // Disable blur-behind if it was enabled for transparent mode
        if (_hwnd != IntPtr.Zero)
        {
            var blurBehind = new DwmBlurBehind
            {
                dwFlags = DWM_BB_ENABLE,
                fEnable = false,
                hRgnBlur = IntPtr.Zero
            };
            DwmEnableBlurBehindWindow(_hwnd, ref blurBehind);

            // Clear any custom system backdrop
            try
            {
                var target = this.As<ICompositionSupportsSystemBackdrop>();
                target.SystemBackdrop = null;
            }
            catch { }
        }
    }

    private void EnsureTransparentBackdrop()
    {
        _lastAppliedBlurIntensity = -1;
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfig = null;

        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        // Step 1: Enable DWM blur-behind with a tiny region to activate alpha compositing
        var region = CreateRectRgn(-2, -2, -1, -1);
        var blurBehind = new DwmBlurBehind
        {
            dwFlags = DWM_BB_ENABLE | DWM_BB_BLURREGION,
            fEnable = true,
            hRgnBlur = region
        };
        DwmEnableBlurBehindWindow(_hwnd, ref blurBehind);

        // Step 2: Clear the Win32 background with black (transparent under DWM blur)
        IntPtr hdc = GetDC(_hwnd);
        if (hdc != IntPtr.Zero)
        {
            if (GetClientRect(_hwnd, out var clientRect))
            {
                IntPtr brush = CreateSolidBrush(0);
                FillRect(hdc, ref clientRect, brush);
                DeleteObject(brush);
            }
            ReleaseDC(_hwnd, hdc);
        }

        // Step 3: Set a transparent composition brush as the system backdrop
        // Must use Windows.UI.Composition (not Microsoft.UI.Composition)
        var compositor = new global::Windows.UI.Composition.Compositor();
        var transparentBrush = compositor.CreateColorBrush(
            global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
        var target = this.As<ICompositionSupportsSystemBackdrop>();
        target.SystemBackdrop = transparentBrush;
    }

    private async Task ApplyRunCatSettingsAsync()
    {
        bool shouldRun = _settings.RunCatEnabled && !_isGameMinimalMode;
        string runner = _settings.RunCatRunner;
        int loadVersion = ++_runCatLoadVersion;

        if (!shouldRun)
        {
            if (_runCatService != null)
            {
                _runCatService.FrameChanged -= OnRunCatFrameChanged;
                _runCatService.Stop();
                _runCatService.Dispose();
                _runCatService = null;
                _runCatFrames = null;
            }

            RunCatButton.Visibility = Visibility.Collapsed;
            RunCatImage.Source = null;
            return;
        }

        if (_runCatService != null
            && _runCatService.RunnerName == runner
            && _runCatFrames is { Length: > 0 }
            && string.Equals(_lastRunCatTintHex, _settings.TopBarForegroundColor, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_runCatService != null)
        {
            _runCatService.FrameChanged -= OnRunCatFrameChanged;
            _runCatService.Stop();
            _runCatService.Dispose();
        }

        _runCatService = new RunCatService();
        _runCatService.Start(runner);

        var tintedFrames = new ImageSource[_runCatService.FrameCount];
        var tintColor = GetTopBarForegroundColor();
        for (int i = 0; i < _runCatService.FrameCount; i++)
        {
            var path = _runCatService.GetFramePath(i);
            tintedFrames[i] = await CreateTintedRunCatFrameAsync(path, tintColor);
        }

        if (loadVersion != _runCatLoadVersion || _runCatService == null || _runCatService.RunnerName != runner)
        {
            return;
        }

        _runCatFrames = tintedFrames;
        _lastRunCatTintHex = _settings.TopBarForegroundColor;
        RunCatImage.Source = _runCatFrames[0];
        RunCatButton.Visibility = Visibility.Visible;
        _runCatService.FrameChanged += OnRunCatFrameChanged;
    }

    private void EnterMinimalMode(int? activeGameProcessId)
    {
        _gamePerformanceService.ApplyGameOptimizations(activeGameProcessId);

        if (_isGameMinimalMode)
        {
            return;
        }

        _isGameMinimalMode = true;
        _clockTimer.Stop();
        UpdateBackgroundMaintenanceState();
        UpdateVisualRefreshState();
        UpdateDiscordDemand();

        _finderWindow?.HideWindow();

        if (_settingsWindow != null)
        {
            _settingsWindow.Close();
            _settingsWindow = null;
        }

        _ = ApplyRunCatSettingsAsync();

        if (!_isHidden)
        {
            _isHidden = true;
            if (_appBarRegistered)
            {
                WindowHelper.UnregisterAppBar(this);
                _appBarRegistered = false;
            }

            WindowHelper.HideWindow(this);
        }
    }

    private void ExitMinimalMode()
    {
        _isGameMinimalMode = false;
        _clockTimer.Start();
        UpdateVisualRefreshState(boost: true);
        UpdateClock();
        _ = ApplyRunCatSettingsAsync();
        UpdateDiscordDemand(boost: true);
        UpdateBackgroundMaintenanceState(boost: true);
    }

    private ModuleDemand EvaluateAdaptiveVisualDemand()
    {
        if (_isGameMinimalMode || _isHidden || _settings.TopBarStyle != "Adaptive")
        {
            return ModuleDemand.Cold("adaptive-disabled");
        }

        if (DateTime.UtcNow - _lastAdaptiveVisualBoostUtc <= AdaptiveVisualHotHoldDuration)
        {
            return ModuleDemand.Hot("adaptive-boost");
        }

        return ModuleDemand.Warm("adaptive-visible");
    }

    private void OnAdaptiveVisualTemperatureChanged(ModuleTemperature previousTemperature, ModuleTemperature nextTemperature, string reason)
    {
        if (nextTemperature == ModuleTemperature.Cold)
        {
            _visualTimer.Stop();
            return;
        }

        _visualTimer.Interval = nextTemperature == ModuleTemperature.Hot
            ? AdaptiveVisualRefreshInterval
            : AdaptiveVisualWarmRefreshInterval;
        _visualTimer.Start();

        if (previousTemperature == ModuleTemperature.Cold || nextTemperature == ModuleTemperature.Hot)
        {
            UpdateAdaptiveBackground(GetEffectiveTopBarOpacity(), true);
        }
    }

    private void UpdateDiscordDemand(bool boost = false)
    {
        if (boost)
        {
            _lastDiscordBoostUtc = DateTime.UtcNow;
        }

        _discordModule.Update(EvaluateDiscordDemand());
    }

    private ModuleDemand EvaluateDiscordDemand()
    {
        if (_isGameMinimalMode || _isHidden || !_settings.DiscordButtonEnabled)
        {
            return ModuleDemand.Cold("discord-hidden");
        }

        if (_discordNotificationService.HasActiveCall ||
            _discordNotificationService.UnreadCount > 0 ||
            (_discordNotificationWindow?.IsMenuVisible ?? false) ||
            DateTime.UtcNow - _lastDiscordBoostUtc <= DiscordHotHoldDuration)
        {
            return ModuleDemand.Hot("discord-engaged");
        }

        return ModuleDemand.Warm("discord-visible");
    }

    private void OnDiscordTemperatureChanged(ModuleTemperature previousTemperature, ModuleTemperature nextTemperature, string reason)
    {
        _discordNotificationService.SetDemand(_discordDemandOwnerId, nextTemperature);
    }

    private ModuleDemand EvaluateBackgroundMaintenanceDemand()
    {
        if (_isGameMinimalMode || !_settings.BackgroundOptimizationEnabled)
        {
            return ModuleDemand.Cold("background-disabled");
        }

        if (DateTime.UtcNow - _lastBackgroundMaintenanceBoostUtc <= BackgroundMaintenanceHotHoldDuration)
        {
            return ModuleDemand.Hot("background-boost");
        }

        return ModuleDemand.Warm("background-idle");
    }

    private void OnBackgroundMaintenanceTemperatureChanged(ModuleTemperature previousTemperature, ModuleTemperature nextTemperature, string reason)
    {
        if (nextTemperature == ModuleTemperature.Cold)
        {
            _backgroundMaintenanceTimer.Stop();

            if (Interlocked.CompareExchange(ref _backgroundMaintenanceInFlight, 0, 0) == 0)
            {
                _ = Task.Run(_gamePerformanceService.RestoreNormalOptimizations);
            }

            return;
        }

        _backgroundMaintenanceTimer.Interval = nextTemperature == ModuleTemperature.Hot
            ? BackgroundMaintenanceInterval
            : BackgroundMaintenanceWarmInterval;

        if (!_backgroundMaintenanceTimer.IsEnabled)
        {
            _backgroundMaintenanceTimer.Start();
        }

        if (nextTemperature == ModuleTemperature.Hot || previousTemperature == ModuleTemperature.Cold)
        {
            QueueBackgroundMaintenance();
        }
    }

    private void OnRunCatFrameChanged(int frame)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_runCatFrames != null && frame < _runCatFrames.Length)
            {
                RunCatImage.Source = _runCatFrames[frame];
            }
        });
    }

    private static async Task<ImageSource> CreateTintedRunCatFrameAsync(string path, global::Windows.UI.Color tintColor)
    {
        var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(global::Windows.Storage.FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        byte[] pixels = pixelData.DetachPixelData();
        for (int index = 0; index < pixels.Length; index += 4)
        {
            byte alpha = pixels[index + 3];
            if (alpha == 0)
            {
                continue;
            }

            pixels[index] = (byte)((tintColor.B * alpha) / 255);
            pixels[index + 1] = (byte)((tintColor.G * alpha) / 255);
            pixels[index + 2] = (byte)((tintColor.R * alpha) / 255);
        }

        var bitmap = new WriteableBitmap((int)decoder.PixelWidth, (int)decoder.PixelHeight);
        using var pixelStream = bitmap.PixelBuffer.AsStream();
        await pixelStream.WriteAsync(pixels);
        pixelStream.Seek(0, SeekOrigin.Begin);
        return bitmap;
    }

    private double GetEffectiveTopBarOpacity()
    {
        if (_settings.TopBarStyle == "Transparent")
        {
            return 0;
        }

        if (_settings.TopBarStyle == "Adaptive")
        {
            return 1.0;
        }

        double opacity = Math.Clamp(_settings.TopBarOpacity, 0.0, 1.0);
        if (opacity <= 0.001)
        {
            return DefaultRenderedTopBarOpacity;
        }

        return Math.Max(opacity, MinimumRenderedTopBarOpacity);
    }

    private double GetEffectiveBlurIntensity()
    {
        double intensity = Math.Clamp(_settings.BlurIntensity, 0.0, 1.0);
        if (intensity <= 0.001)
        {
            return DefaultRenderedBlurIntensity;
        }

        return Math.Max(intensity, MinimumRenderedBlurIntensity);
    }

    private void UpdateWindowRegion()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_settings.TopBarStyle != "Transparent")
        {
            if (_lastWindowRegionSignature.Length == 0)
            {
                return;
            }

            SetWindowRgn(_hwnd, IntPtr.Zero, true);
            _lastWindowRegionSignature = string.Empty;
            return;
        }

        IReadOnlyList<WindowRegionSegment> regions = CollectTransparentRegions();
        if (regions.Count == 0)
        {
            if (_lastWindowRegionSignature.Length == 0)
            {
                return;
            }

            SetWindowRgn(_hwnd, IntPtr.Zero, true);
            _lastWindowRegionSignature = string.Empty;
            return;
        }

        string signature = string.Join("|", regions.Select(static region =>
            $"{region.Left},{region.Top},{region.Right},{region.Bottom}"));
        if (string.Equals(signature, _lastWindowRegionSignature, StringComparison.Ordinal))
        {
            return;
        }

        IntPtr combinedRegion = CreateRectRgn(0, 0, 0, 0);
        if (combinedRegion == IntPtr.Zero)
        {
            return;
        }

        bool applied = false;
        try
        {
            foreach (WindowRegionSegment region in regions)
            {
                IntPtr segmentRegion = CreateRectRgn(region.Left, region.Top, region.Right, region.Bottom);
                if (segmentRegion == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    CombineRgn(combinedRegion, combinedRegion, segmentRegion, RGN_OR);
                    applied = true;
                }
                finally
                {
                    DeleteObject(segmentRegion);
                }
            }

            if (!applied)
            {
                return;
            }

            SetWindowRgn(_hwnd, combinedRegion, true);
            combinedRegion = IntPtr.Zero;
            _lastWindowRegionSignature = signature;
        }
        finally
        {
            if (combinedRegion != IntPtr.Zero)
            {
                DeleteObject(combinedRegion);
            }
        }
    }

    private IReadOnlyList<WindowRegionSegment> CollectTransparentRegions()
    {
        List<WindowRegionSegment> regions = [];

        AddContentRegion(regions, MenuButton, useElementBounds: true, paddingX: 1, paddingY: 1);
        AddContentRegion(regions, FinderButton, useElementBounds: true, paddingX: 8, paddingY: 5);
        AddContentRegion(regions, WeatherButton, useElementBounds: false, paddingX: 1, paddingY: 1);
        AddContentRegion(regions, DiscordButton, useElementBounds: false, paddingX: 1, paddingY: 1);
        AddContentRegion(regions, MusicButton, useElementBounds: false, paddingX: 1, paddingY: 1);
        AddContentRegion(regions, RunCatButton, useElementBounds: false, paddingX: 1, paddingY: 1);
        AddContentRegion(regions, PanelThemeButton, useElementBounds: false, paddingX: 1, paddingY: 1);
        AddElementRegion(regions, ClockText, paddingX: 1, paddingY: 1);

        foreach (Button shortcutButton in ShortcutButtonsPanel.Children.OfType<Button>())
        {
            AddContentRegion(regions, shortcutButton, useElementBounds: true, paddingX: 8, paddingY: 5);
        }

        return regions;
    }

    private void AddContentRegion(List<WindowRegionSegment> regions, Button button, bool useElementBounds, double paddingX, double paddingY)
    {
        if (button.Visibility != Visibility.Visible)
        {
            return;
        }

        FrameworkElement target = useElementBounds
            ? button
            : button.Content as FrameworkElement ?? button;

        AddElementRegion(regions, target, paddingX, paddingY);
    }

    private void AddElementRegion(List<WindowRegionSegment> regions, FrameworkElement element, double paddingX, double paddingY)
    {
        if (!TryCreateWindowRegionSegment(element, paddingX, paddingY, out WindowRegionSegment region))
        {
            return;
        }

        regions.Add(region);
    }

    private bool TryCreateWindowRegionSegment(FrameworkElement element, double paddingX, double paddingY, out WindowRegionSegment region)
    {
        region = default;

        if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            GeneralTransform transform = element.TransformToVisual(RootPanel);
            var topLeft = transform.TransformPoint(new global::Windows.Foundation.Point(0, 0));
            var bottomRight = transform.TransformPoint(new global::Windows.Foundation.Point(element.ActualWidth, element.ActualHeight));

            int left = Math.Max(0, (int)Math.Floor(Math.Min(topLeft.X, bottomRight.X) - paddingX));
            int top = Math.Max(0, (int)Math.Floor(Math.Min(topLeft.Y, bottomRight.Y) - paddingY));
            int right = Math.Min((int)Math.Ceiling(RootPanel.ActualWidth), (int)Math.Ceiling(Math.Max(topLeft.X, bottomRight.X) + paddingX));
            int bottom = Math.Min((int)Math.Ceiling(RootPanel.ActualHeight), (int)Math.Ceiling(Math.Max(topLeft.Y, bottomRight.Y) + paddingY));

            if (right <= left || bottom <= top)
            {
                return false;
            }

            region = new WindowRegionSegment(left, top, right, bottom);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (byte R, byte G, byte B) ParseHexColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length < 7)
        {
            return (0, 0, 0);
        }

        try
        {
            byte r = Convert.ToByte(hex[1..3], 16);
            byte g = Convert.ToByte(hex[3..5], 16);
            byte b = Convert.ToByte(hex[5..7], 16);
            return (r, g, b);
        }
        catch
        {
            return (0, 0, 0);
        }
    }

    private SolidColorBrush CreateBlurBackgroundBrush()
    {
        return new SolidColorBrush(global::Windows.UI.Color.FromArgb(
            (byte)Math.Round(GetEffectiveTopBarOpacity() * 28), 255, 255, 255));
    }

    private void UpdateGlassPanels(bool visible)
    {
        if (visible && ShortcutButtonsPanel.Children.Count > 0)
        {
            ShortcutGlass.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(18, 255, 255, 255));
            ShortcutGlass.BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(30, 255, 255, 255));
            ShortcutGlass.BorderThickness = new Thickness(0.8);
            ShortcutGlass.Padding = new Thickness(3, 2, 3, 2);
        }
        else
        {
            ShortcutGlass.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ShortcutGlass.BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ShortcutGlass.BorderThickness = new Thickness(0);
            ShortcutGlass.Padding = new Thickness(0);
        }
    }

    private global::Windows.UI.Color GetTopBarForegroundColor(byte alpha = 255)
    {
        var (r, g, b) = ParseHexColor(_settings.TopBarForegroundColor);
        return global::Windows.UI.Color.FromArgb(alpha, r, g, b);
    }

    private static bool UseDarkTopBarForeground(global::Windows.UI.Color color)
    {
        double luminance = (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);
        return luminance < 140;
    }

    private static void ApplyIconButtonResources(Button button, SolidColorBrush hoverBrush, SolidColorBrush pressedBrush)
    {
        button.Resources["ButtonBackgroundPointerOver"] = hoverBrush;
        button.Resources["ButtonBackgroundPressed"] = pressedBrush;
    }

    private void UpdateAdaptiveBackground(double effectiveTopBarOpacity, bool force = false)
    {
        using var perfScope = PerformanceLogger.Measure("TopBar.UpdateAdaptiveBackground", 1.5);
        var adaptiveColor = SampleScreenColorBelowTopBar();

        if (!force && AreColorsClose(adaptiveColor, _lastAdaptiveColor))
        {
            return;
        }

        _lastAdaptiveColor = adaptiveColor;

        if (_acrylicController != null)
        {
            _lastAppliedBlurIntensity = -1;
            _acrylicController.Dispose();
            _acrylicController = null;
            _backdropConfig = null;
        }

        RootPanel.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(
            255, adaptiveColor.R, adaptiveColor.G, adaptiveColor.B));
    }

    private static bool AreColorsClose(global::Windows.UI.Color? left, global::Windows.UI.Color? right)
    {
        if (!left.HasValue || !right.HasValue)
        {
            return left.HasValue == right.HasValue;
        }

        return Math.Abs(left.Value.R - right.Value.R) <= 3
            && Math.Abs(left.Value.G - right.Value.G) <= 3
            && Math.Abs(left.Value.B - right.Value.B) <= 3;
    }

    private global::Windows.UI.Color SampleScreenColorBelowTopBar()
    {
        using var perfScope = PerformanceLogger.Measure("TopBar.SampleScreenColorBelowTopBar", 1.0);
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return global::Windows.UI.Color.FromArgb(255, 20, 20, 22);
        }

        try
        {
            int screenWidth = _screen.Right - _screen.Left;
            // Sample the pixel row right at the topbar's bottom edge
            int sampleY = _screen.Top + BarHeight;

            Span<int> xOffsets = stackalloc int[]
            {
                _screen.Left + (int)(screenWidth * 0.10),
                _screen.Left + (int)(screenWidth * 0.25),
                _screen.Left + (int)(screenWidth * 0.40),
                _screen.Left + screenWidth / 2,
                _screen.Left + (int)(screenWidth * 0.60),
                _screen.Left + (int)(screenWidth * 0.75),
                _screen.Left + (int)(screenWidth * 0.90)
            };

            const int maxSamples = 7;
            Span<uint> samples = stackalloc uint[maxSamples];
            int sampleCount = 0;

            foreach (int x in xOffsets)
            {
                uint colorValue = GetPixel(screenDc, x, sampleY);
                if (colorValue != 0xFFFFFFFF)
                {
                    samples[sampleCount++] = colorValue;
                }
            }

            if (sampleCount == 0)
            {
                return global::Windows.UI.Color.FromArgb(255, 20, 20, 22);
            }

            // Find the most common color among samples
            uint bestColor = samples[0];
            int bestVotes = 0;

            for (int i = 0; i < sampleCount; i++)
            {
                int votes = 0;
                for (int j = 0; j < sampleCount; j++)
                {
                    if (ColorsWithinTolerance(samples[i], samples[j], 15))
                    {
                        votes++;
                    }
                }
                if (votes > bestVotes)
                {
                    bestVotes = votes;
                    bestColor = samples[i];
                }
            }

            byte r = (byte)(bestColor & 0xFF);
            byte g = (byte)((bestColor >> 8) & 0xFF);
            byte b = (byte)((bestColor >> 16) & 0xFF);
            return global::Windows.UI.Color.FromArgb(255, r, g, b);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static bool ColorsWithinTolerance(uint a, uint b, int tolerance)
    {
        int dr = (int)(a & 0xFF) - (int)(b & 0xFF);
        int dg = (int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF);
        int db = (int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF);
        return Math.Abs(dr) <= tolerance && Math.Abs(dg) <= tolerance && Math.Abs(db) <= tolerance;
    }

}
