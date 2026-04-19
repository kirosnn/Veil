using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using System.Diagnostics;
using System.Globalization;
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
    private const double RightAlignedTrailingGap = 10;
    private const double RightAlignedEdgeInset = 2;
    private const double DefaultRenderedTopBarOpacity = 0.92;
    private const double MinimumRenderedTopBarOpacity = 0.04;
    private const double DefaultRenderedBlurIntensity = 0.55;
    private const double MinimumRenderedBlurIntensity = 0.20;
    private static readonly TimeSpan ShowAfterHideDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MinimumVisibleDurationBeforeHide = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan AdaptiveVisualWarmRefreshInterval = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan AdaptiveVisualRefreshInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AdaptiveVisualDeepSampleInterval = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan AdaptiveClearTransitionDelay = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan AdaptiveOpaqueTransitionDelay = TimeSpan.FromMilliseconds(150);
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
    private static readonly DiscordNotificationService SharedDiscordNotificationService = new();
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _visualTimer;
    private readonly DemandDrivenModule _adaptiveVisualModule;
    private readonly DemandDrivenModule _discordModule;
    private readonly DemandDrivenModule _backgroundMaintenanceModule;
    private readonly string _monitorId;
    private ScreenBounds _screen;
    private readonly AppSettings _settings;
    private readonly string _discordDemandOwnerId;
    private bool _startHiddenUntilReady;
    private bool _ownsGlobalHotkeys;
    private bool _isHidden;
    private bool _isGameMinimalMode;
    private bool _appBarRegistered;
    private IntPtr _hwnd;
    private global::Windows.UI.Color? _lastAdaptiveColor;
    private global::Windows.UI.Color? _lastAdaptiveProbeColor;
    private global::Windows.UI.Color? _adaptiveForegroundOverride;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private FinderWindow? _finderWindow;
    private FinderHotkeyService? _finderHotkeyService;
    private SettingsWindow? _settingsWindow;
    private SystemStatsWindow? _systemStatsWindow;
    private MusicControlWindow? _musicControlWindow;
    private DiscordNotificationWindow? _discordNotificationWindow;
    private readonly DiscordNotificationService _discordNotificationService;
    private TerminalWindow? _terminalWindow;
    private readonly MediaControlService _mediaControlService = new();
    private RunCatService? _runCatService;
    private readonly GamePerformanceService _gamePerformanceService;
    private Button[] _shortcutButtons = [];
    private ImageSource?[]? _runCatFrames;
    private int _runCatLoadVersion;
    private int _musicAlbumArtLoadVersion;
    private string? _lastRunCatTintHex;
    private readonly DispatcherTimer _backgroundMaintenanceTimer;
    private int _backgroundMaintenanceInFlight;
    private string _lastWindowRegionSignature = string.Empty;
    private double _lastKnownClockWidth = MinimumClockClearance;
    private string _lastClockText = string.Empty;
    private string _lastSettingsSignature = string.Empty;
    private string _lastShortcutGlassBlurRegionSignature = string.Empty;
    private bool _isAdaptiveClearModeActive;
    private bool _hasAdaptiveModeState;
    private bool? _pendingAdaptiveClearMode;
    private DateTime _pendingAdaptiveClearModeUtc = DateTime.MinValue;
    private DateTime _lastAdaptiveVisualBoostUtc = DateTime.MinValue;
    private DateTime _lastAdaptiveDeepSampleUtc = DateTime.MinValue;
    private DateTime _lastAdaptiveClearSampleUtc = DateTime.MinValue;
    private DateTime _lastDiscordBoostUtc = DateTime.MinValue;
    private DateTime _lastBackgroundMaintenanceBoostUtc = DateTime.MinValue;
    private double _lastAppliedBlurIntensity = -1;
    private DateTime _pendingShowUtc = DateTime.MinValue;
    private DateTime _lastShownUtc = DateTime.MinValue;

    internal TopBarWindow(string monitorId, ScreenBounds screen, GamePerformanceService gamePerformanceService, bool ownsGlobalHotkeys, bool startHiddenUntilReady = false)
    {
        _monitorId = monitorId;
        _screen = screen;
        _gamePerformanceService = gamePerformanceService;
        _settings = AppSettings.Current;
        _startHiddenUntilReady = startHiddenUntilReady;
        _ownsGlobalHotkeys = ownsGlobalHotkeys;
        _discordDemandOwnerId = $"topbar:{monitorId}";
        _discordNotificationService = SharedDiscordNotificationService;

        InitializeComponent();
        Title = "Veil TopBar";
        if (_startHiddenUntilReady)
        {
            RootPanel.Opacity = 0;
        }

        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _clockTimer.Tick += OnClockTick;
        _clockTimer.Start();
        UpdateClock();

        _visualTimer = new DispatcherTimer
        {
            Interval = AdaptiveVisualRefreshInterval
        };
        _visualTimer.Tick += OnVisualTick;

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
        _lastSettingsSignature = CreateSettingsSignature();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnActivated;

        _hwnd = WindowHelper.GetHwnd(this);
        bool revealAfterSetup = _startHiddenUntilReady;

        WindowHelper.RemoveTitleBar(this);
        WindowHelper.ExtendFrameIntoClientArea(this);
        WindowHelper.MakeOverlay(this);
        if (revealAfterSetup)
        {
            ShowWindowNative(_hwnd, SW_HIDE);
        }

        ApplyTopBarPlacement(true);
        ApplySettings();

        RebuildShortcutButtons();
        _ = InstalledAppService.PreloadAsync();
        _ = ApplyRunCatSettingsAsync();
        _ = InitMediaControlAsync();
        _ = InitDiscordNotificationsAsync();
        ApplyHotkeyOwnership();
        UpdateVisualRefreshState(boost: true);
        UpdateDiscordDemand(boost: true);
        UpdateBackgroundMaintenanceState(boost: true);
        DispatcherQueue.TryEnqueue(PrewarmTransientWindows);
        if (revealAfterSetup)
        {
            RootPanel.Opacity = 1;
            ShowWindowNative(_hwnd, SW_SHOWNOACTIVATE);
            _startHiddenUntilReady = false;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        AppLogger.Info($"TopBarWindow closed for {_monitorId}. Hidden={_isHidden} MinimalMode={_isGameMinimalMode}.");
        _clockTimer.Stop();
        _visualTimer.Stop();
        _backgroundMaintenanceTimer.Stop();
        _settings.Changed -= OnSettingsChanged;
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
        DisposeDictationHotkey();
        _runCatService?.Dispose();
        _gamePerformanceService.RestoreNormalOptimizations();
        _runCatService = null;
        if (_appBarRegistered)
        {
            WindowHelper.UnregisterAppBar(this);
        }
    }

    internal ScreenBounds ScreenBounds => _screen;

    internal IntPtr WindowHandle => _hwnd;

    internal bool IsMinimalModeActive => _isGameMinimalMode;

    internal void UpdateScreenBounds(ScreenBounds screen)
    {
        if (_screen == screen)
        {
            return;
        }

        _screen = screen;

        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (!_isHidden && !_isGameMinimalMode)
        {
            ApplyTopBarPlacement(true);
        }

        UpdateTopBarLayout();
    }

    private int GetBarHeightInPhysicalPixels()
        => Math.Max(1, WindowHelper.ViewPixelsToPhysical(this, BarHeight));

    private double GetScreenWidthInViewPixels()
        => WindowHelper.PhysicalPixelsToView(this, _screen.Right - _screen.Left);

    private void ApplyTopBarPlacement(bool refreshAppBar)
    {
        int height = GetBarHeightInPhysicalPixels();

        if (refreshAppBar && _appBarRegistered)
        {
            WindowHelper.UnregisterAppBar(this);
            _appBarRegistered = false;
        }

        WindowHelper.SetAlwaysOnTop(this);
        global::Windows.Graphics.RectInt32 bounds = WindowHelper.RegisterAppBar(this, _screen, ABE_TOP, height);
        WindowHelper.PositionOnMonitor(this, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        _appBarRegistered = true;
    }

    internal void ApplyActivityState(bool gameRunning, bool shouldHideForForegroundWindow, int? activeGameProcessId)
    {
        try
        {
            bool visibilityChanged = false;
            if (gameRunning)
            {
                _pendingShowUtc = DateTime.MinValue;
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
                if (DateTime.UtcNow - _lastShownUtc < MinimumVisibleDurationBeforeHide)
                {
                    return;
                }

                _pendingShowUtc = DateTime.MinValue;
                _isHidden = true;
                visibilityChanged = true;
                AppLogger.Info($"TopBarWindow hiding for {_monitorId}. Foreground game fullscreen={shouldHideForForegroundWindow}.");
                if (_appBarRegistered)
                {
                    WindowHelper.UnregisterAppBar(this);
                    _appBarRegistered = false;
                }
                WindowHelper.HideWindow(this);
            }
            else if (shouldHideWindow && _isHidden)
            {
                _pendingShowUtc = DateTime.MinValue;
            }
            else if (!shouldHideWindow && _isHidden)
            {
                if (_pendingShowUtc == DateTime.MinValue)
                {
                    _pendingShowUtc = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - _pendingShowUtc >= ShowAfterHideDelay)
                {
                    _pendingShowUtc = DateTime.MinValue;
                    _isHidden = false;
                    _lastShownUtc = DateTime.UtcNow;
                    visibilityChanged = true;
                    AppLogger.Info($"TopBarWindow showing for {_monitorId}.");
                    WindowHelper.ShowWindow(this);
                    ApplyTopBarPlacement(true);
                }
            }
            else
            {
                _pendingShowUtc = DateTime.MinValue;
            }

            if (visibilityChanged)
            {
                UpdateVisualRefreshState(boost: !_isHidden);
                UpdateDiscordDemand(boost: !_isHidden);
                UpdateBackgroundMaintenanceState(boost: !_isHidden);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"TopBarWindow ApplyActivityState failed for {_monitorId}.", ex);
        }
    }

    private void OnClockTick(object? sender, object e)
    {
        try
        {
            using var perfScope = PerformanceLogger.Measure("TopBar.OnClockTick", 1.5);
            UpdateClock();
            UpdateVisualRefreshState();
            UpdateDiscordDemand();
            UpdateBackgroundMaintenanceState();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"TopBarWindow clock tick failed for {_monitorId}.", ex);
        }
    }

    private void UpdateClock()
    {
        if (_isGameMinimalMode)
        {
            return;
        }

        var now = DateTime.Now;
        string dayName = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(now.DayOfWeek);
        dayName = string.IsNullOrWhiteSpace(dayName) ? now.ToString("ddd", CultureInfo.CurrentCulture) : dayName;
        dayName = dayName.TrimEnd('.');
        dayName = dayName.Length > 0
            ? char.ToUpper(dayName[0], CultureInfo.CurrentCulture) + dayName[1..].ToLower(CultureInfo.CurrentCulture)
            : now.ToString("ddd", CultureInfo.CurrentCulture);
        string formatted = $"{dayName}. {now:dd MMMM  HH:mm}";
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
            string settingsSignature = CreateSettingsSignature();
            if (string.Equals(settingsSignature, _lastSettingsSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastSettingsSignature = settingsSignature;
            ApplySettings();
            RebuildShortcutButtons();
            _finderHotkeyService?.SetEnabled(_settings.FinderHotkeyEnabled);
            _musicControlWindow?.RefreshLayout();
            _discordNotificationWindow?.RefreshLayout();
            _systemStatsWindow?.RefreshAppearance();
            UpdateMusicButtonVisibility();
            UpdateDiscordButtonVisibility();
            UpdateDiscordDemand(boost: true);
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
                ResetAdaptiveModeState();
                _adaptiveForegroundOverride = null;
                EnsureTransparentBackdrop();
                WindowHelper.DisableLayeredTransparency(this);
                RootPanel.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
                break;
            case "Blur":
                ResetAdaptiveModeState();
                _adaptiveForegroundOverride = null;
                EnsureTopBarAcrylic();
                WindowHelper.DisableLayeredTransparency(this);
                RootPanel.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
                break;
            case "Adaptive":
                ResetAdaptiveModeState();
                UpdateAdaptiveBackground(effectiveTopBarOpacity, true);
                break;
            default:
                ResetAdaptiveModeState();
                _adaptiveForegroundOverride = null;
                RemoveTopBarAcrylic();
                WindowHelper.DisableLayeredTransparency(this);
                var solidColor = ParseHexColor(_settings.SolidColor);
                RootPanel.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(
                    (byte)Math.Round(effectiveTopBarOpacity * 255), solidColor.R, solidColor.G, solidColor.B));
                break;
        }

        UpdateGlassPanels(true);

        UpdateVisualRefreshState(boost: true);
        UpdateWindowRegion();

        ApplyForegroundTheme();

        UpdateBackgroundMaintenanceState(boost: true);
    }

    private string CreateSettingsSignature()
    {
        string shortcutSignature = string.Join(
            "|",
            _settings.ShortcutButtons.Select(static shortcut =>
                shortcut is null
                    ? "-"
                    : $"{shortcut.AppId}:{shortcut.DisplayName}:{shortcut.AppName}"));

        return string.Join(
            ";",
            _settings.TopBarStyle,
            _settings.TopBarOpacity.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
            _settings.BlurIntensity.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
            _settings.TopBarContentAlignment,
            _settings.SolidColor,
            _settings.TopBarForegroundColor,
            _settings.ShowFinderBubble,
            _settings.FinderBubbleOpacity.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
            _settings.FinderHotkeyEnabled,
            _settings.DiscordButtonEnabled,
            _settings.MusicButtonEnabled,
            _settings.MusicShowVolume,
            _settings.MusicShowSourceToggle,
            _settings.ShowAppButtonOutline,
            _settings.RunCatEnabled,
            _settings.RunCatRunner,
            _settings.BackgroundOptimizationEnabled,
            shortcutSignature);
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

    private void UpdateTopBarLayout()
    {
        UpdateCenterContentPlacement();
        UpdateActionButtonLayout();
        UpdateWindowRegion();
    }

    private void UpdateActionButtonLayout()
    {
        double screenWidth = GetScreenWidthInViewPixels();
        double leftMargin = LeftButtonsPanel.Margin.Left + LeftButtonsPanel.Margin.Right;
        double rightWidth = RightButtonsPanel.ActualWidth + RightButtonsPanel.Margin.Left + RightButtonsPanel.Margin.Right;
        double centerClearance = _settings.TopBarContentAlignment == "Center"
            ? GetCenterReservedWidth()
            : 0;
        double maxLeftWidth = Math.Max(0, screenWidth - rightWidth - centerClearance - leftMargin);

        LeftButtonsPanel.MaxWidth = maxLeftWidth;

        double finderMaxWidth = Math.Clamp(maxLeftWidth * 0.42, 72, 180);
        FinderButton.MaxWidth = finderMaxWidth;
        FinderButtonText.MaxWidth = Math.Max(24, finderMaxWidth - FinderButton.Padding.Left - FinderButton.Padding.Right);

        if (_shortcutButtons.Length == 0)
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
            (availableShortcutWidth - (Math.Max(0, _shortcutButtons.Length - 1) * ShortcutButtonsPanel.Spacing)) / _shortcutButtons.Length);
        double buttonMaxWidth = Math.Min(130, availablePerButtonWidth);
        double textMaxWidth = Math.Max(0, buttonMaxWidth - 20);

        foreach (Button button in _shortcutButtons)
        {
            button.MaxWidth = buttonMaxWidth;

            if (button.Content is TextBlock textBlock)
            {
                textBlock.MaxWidth = textMaxWidth;
            }
        }
    }

    private void UpdateCenterContentPlacement()
    {
        double trailingContentWidth = GetTrailingContentWidth();

        if (_settings.TopBarContentAlignment == "Right")
        {
            double reservedTrailingWidth = trailingContentWidth + RightAlignedTrailingGap + Math.Abs(_settings.ClockOffset);
            RightButtonsPanel.Margin = new Thickness(0, 0, reservedTrailingWidth, 0);
            ClockText.HorizontalAlignment = HorizontalAlignment.Right;
            ClockText.Margin = new Thickness(0, 0, Math.Max(0, RightAlignedEdgeInset - _settings.ClockOffset), 0);
            TerminalTabPanel.HorizontalAlignment = HorizontalAlignment.Right;
            TerminalTabPanel.Margin = new Thickness(0, 0, RightAlignedEdgeInset, 0);
        }
        else
        {
            RightButtonsPanel.Margin = new Thickness(0, 0, 8, 0);
            ClockText.HorizontalAlignment = HorizontalAlignment.Center;
            ClockText.Margin = new Thickness(_settings.ClockOffset, 0, 0, 0);
            TerminalTabPanel.HorizontalAlignment = HorizontalAlignment.Center;
            TerminalTabPanel.Margin = new Thickness(0);
        }

        Canvas.SetZIndex(ClockText, 2);
        Canvas.SetZIndex(TerminalTabPanel, 2);
    }

    private double GetCenterReservedWidth()
    {
        return Math.Max(
            MinimumClockClearance,
            GetTrailingContentWidth() + 28 + (Math.Abs(_settings.ClockOffset) * 2));
    }

    private double GetTrailingContentWidth()
    {
        if (TerminalTabPanel.Visibility == Visibility.Visible && TerminalTabPanel.ActualWidth > 0)
        {
            return TerminalTabPanel.ActualWidth;
        }

        if (ClockText.Visibility == Visibility.Visible)
        {
            if (ClockText.ActualWidth > 0)
            {
                _lastKnownClockWidth = ClockText.ActualWidth;
            }
            return _lastKnownClockWidth;
        }

        return MinimumClockClearance;
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
}
