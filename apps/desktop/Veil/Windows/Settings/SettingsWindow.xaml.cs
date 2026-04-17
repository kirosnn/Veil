using Veil.Configuration;
using Veil.Diagnostics;
using Veil.Interop;
using Veil.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WinRT;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

internal sealed record ShortcutOption(string Name, string AppId);
internal sealed record MonitorSelectionOption(string Id, string Label, bool IsPrimary);

public sealed partial class SettingsWindow : Window
{
    private static readonly TimeSpan SessionKeepAliveDuration = TimeSpan.FromMinutes(10);
    private static SettingsSessionSnapshot? s_lastSessionSnapshot;
    private readonly ScreenBounds _screen;
    private readonly string _preferredMonitorId;
    private readonly AppSettings _settings;
    private readonly AiSecretStore _aiSecretStore;
    private readonly AiModelCatalogService _aiModelCatalogService;
    private readonly LocalSpeechModelStore _localSpeechModelStore;
    private readonly DispatcherTimer _sessionKeepAliveTimer;
    private FinderAiWindow? _finderAiWindow;
    private IntPtr _hwnd;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private bool _isInitializing;
    private bool _showRequested;
    private bool _persistSessionStateOnClose = true;
    private DateTime _openedAtUtc;
    private string _selectedSection = "TopBar";
    private double _retainedVerticalOffset;
    private bool _useDarkForeground;
    private IReadOnlyList<ShortcutOption> _shortcutOptions =
    [
        new ShortcutOption("None", string.Empty)
    ];
    private IReadOnlyList<MonitorSelectionOption> _monitorOptions = [];
    private CancellationTokenSource? _speechModelDownloadCancellationSource;
    private CancellationTokenSource? _aiModelCatalogCancellationSource;
    private string? _activeSpeechModelDownloadId;
    private string? _speechModelMessageModelId;
    private string _speechModelMessage = string.Empty;
    private double _speechModelProgressPercent;
    private IReadOnlyList<AiModelCatalogEntry> _aiModelOptions = [];

    internal event EventHandler? SessionExpired;

    internal SettingsWindow(ScreenBounds screen, string preferredMonitorId)
    {
        _screen = screen;
        _preferredMonitorId = preferredMonitorId;
        _settings = AppSettings.Current;
        _aiSecretStore = new AiSecretStore();
        _aiModelCatalogService = new AiModelCatalogService();
        _localSpeechModelStore = new LocalSpeechModelStore();

        InitializeComponent();
        Title = "Veil Settings";

        _sessionKeepAliveTimer = new DispatcherTimer
        {
            Interval = SessionKeepAliveDuration
        };
        _sessionKeepAliveTimer.Tick += OnSessionKeepAliveElapsed;

        Activated += OnFirstActivated;
        Activated += OnWindowActivated;
        Closed += OnClosed;
        SizeChanged += OnSettingsWindowSizeChanged;
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        try
        {
            AppLogger.Info("SettingsWindow first activation started.");
            _hwnd = WindowHelper.GetHwnd(this);
            ConfigureWindowChrome();

            SetupAcrylic();
            ApplyWindowSize();
            LoadSettings();
            _ = LoadDetectedGamesAsync();
            _ = LoadInstalledAppsAsync();
            AppLogger.Info("SettingsWindow first activation completed.");

            if (_showRequested)
            {
                DispatcherQueue.TryEnqueue(ShowCenteredCore);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("SettingsWindow failed during first activation.", ex);
            throw;
        }
    }

    private void SetupAcrylic()
    {
        _acrylicController?.Dispose();

        _acrylicController = new DesktopAcrylicController
        {
            TintColor = global::Windows.UI.Color.FromArgb(255, 34, 40, 50),
            TintOpacity = 0.16f,
            LuminosityOpacity = 0.58f,
            FallbackColor = global::Windows.UI.Color.FromArgb(216, 22, 27, 34)
        };

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark
        };

        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
        PanelBorder.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(18, 255, 255, 255));
        PanelBorder.BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(24, 255, 255, 255));
        PanelBorder.BorderThickness = new Thickness(1);
    }

    private void ConfigureWindowChrome()
    {
        WindowHelper.ApplyAppIcon(this);
        WindowHelper.RemoveTitleBar(this);

        int rounded = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref rounded, sizeof(int));
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated) return;
        if ((DateTime.UtcNow - _openedAtUtc).TotalMilliseconds < 300) return;
        HideWindow();
    }

    private void LoadSettings()
    {
        _isInitializing = true;
        _settings.GameDetectionMode = GameDetectionService.HybridMode;
        LoadMonitorOptions();

        TopBarOpacitySlider.Minimum = 0;
        TopBarOpacitySlider.Maximum = 1.0;
        TopBarOpacitySlider.StepFrequency = 0.01;

        ClockOffsetSlider.Minimum = -40;
        ClockOffsetSlider.Maximum = 40;
        ClockOffsetSlider.StepFrequency = 1;

        ClockBalanceSlider.Minimum = 0;
        ClockBalanceSlider.Maximum = 1;
        ClockBalanceSlider.StepFrequency = 0.01;

        MenuTintSlider.Minimum = 0.04;
        MenuTintSlider.Maximum = 0.3;
        MenuTintSlider.StepFrequency = 0.01;

        FinderBubbleSlider.Minimum = 0.04;
        FinderBubbleSlider.Maximum = 0.3;
        FinderBubbleSlider.StepFrequency = 0.01;

        BlurIntensitySlider.Minimum = 0;
        BlurIntensitySlider.Maximum = 1;
        BlurIntensitySlider.StepFrequency = 0.01;

        TopBarOpacitySlider.Value = _settings.TopBarOpacity;
        ClockOffsetSlider.Value = _settings.ClockOffset;
        ClockBalanceSlider.Value = _settings.ClockBalance;
        MenuTintSlider.Value = _settings.MenuTintOpacity;
        FinderBubbleSlider.Value = _settings.FinderBubbleOpacity;
        BlurIntensitySlider.Value = _settings.BlurIntensity;
        GameProcessesTextBox.Text = string.Join(Environment.NewLine, _settings.GameProcessNames);
        SolidColorTextBox.Text = _settings.SolidColor;
        TopBarForegroundColorTextBox.Text = _settings.TopBarForegroundColor;
        InitializeAiSpeechModelPicker();

        SyncLabels();
        UpdateSectionUi();
        ApplyShortcutSelections();
        UpdateContentHostWidth();
        _isInitializing = false;
    }

    internal void ShowCentered()
    {
        try
        {
            AppLogger.Info("SettingsWindow.ShowCentered called.");
            if (_hwnd == IntPtr.Zero)
            {
                _showRequested = true;
                Activate();
                return;
            }

            ShowCenteredCore();
        }
        catch (Exception ex)
        {
            AppLogger.Error("SettingsWindow.ShowCentered failed.", ex);
            throw;
        }
    }

    private void ShowCenteredCore()
    {
        AppLogger.Info("SettingsWindow.ShowCenteredCore started.");
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        _showRequested = false;
        bool resumeSession = ShouldResumeSession();
        CancelSessionKeepAlive();
        _openedAtUtc = DateTime.UtcNow;
        ApplyWindowSize();
        if (resumeSession)
        {
            RestoreCachedSessionState();
        }
        else
        {
            ClearRetainedSessionState();
            UpdateSectionUi();
            ContentScrollViewer.ChangeView(null, 0, null, true);
        }
        Activate();
        SetForegroundWindow(WindowHelper.GetHwnd(this));
        ApplyReadableContrast();
        RestoreRetainedViewport();
        AppLogger.Info("SettingsWindow.ShowCenteredCore completed.");
    }

    private void OnTopBarOpacityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.TopBarOpacity = e.NewValue;
        SyncLabels();
    }

    private void OnClockOffsetChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.ClockOffset = (int)Math.Round(e.NewValue);
        SyncLabels();
    }

    private void OnClockBalanceChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.ClockBalance = e.NewValue;
        SyncLabels();
    }

    private void OnMenuTintChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.MenuTintOpacity = e.NewValue;
        SyncLabels();
    }

    private void OnBlurIntensityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.BlurIntensity = e.NewValue;
        SyncLabels();
    }

    private void OnSolidColorChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (!TryGetCommittedHexColor(SolidColorTextBox.Text, out string hex))
        {
            SyncSolidColorPreview();
            return;
        }

        _settings.SolidColor = hex;
        SyncSolidColorPreview();
    }

    private void OnSolidColorPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex })
        {
            return;
        }

        _settings.SolidColor = hex;
        _isInitializing = true;
        SolidColorTextBox.Text = _settings.SolidColor;
        _isInitializing = false;
        SyncSolidColorPreview();
    }

    private void OnTopBarForegroundColorChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (!TryGetCommittedHexColor(TopBarForegroundColorTextBox.Text, out string hex))
        {
            SyncTopBarForegroundColorPreview();
            return;
        }

        _settings.TopBarForegroundColor = hex;
        SyncTopBarForegroundColorPreview();
    }

    private void OnTopBarForegroundColorPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex })
        {
            return;
        }

        _settings.TopBarForegroundColor = hex;
        _isInitializing = true;
        TopBarForegroundColorTextBox.Text = _settings.TopBarForegroundColor;
        _isInitializing = false;
        SyncTopBarForegroundColorPreview();
    }

    private void SyncSolidColorPreview()
    {
        try
        {
            string hex = _settings.SolidColor;
            if (hex.Length == 7)
            {
                byte r = Convert.ToByte(hex[1..3], 16);
                byte g = Convert.ToByte(hex[3..5], 16);
                byte b = Convert.ToByte(hex[5..7], 16);
                SolidColorPreview.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    global::Windows.UI.Color.FromArgb(255, r, g, b));
            }
        }
        catch
        {
            SolidColorPreview.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                global::Windows.UI.Color.FromArgb(255, 0, 0, 0));
        }
    }

    private void SyncTopBarForegroundColorPreview()
    {
        try
        {
            string hex = _settings.TopBarForegroundColor;
            if (hex.Length == 7)
            {
                byte r = Convert.ToByte(hex[1..3], 16);
                byte g = Convert.ToByte(hex[3..5], 16);
                byte b = Convert.ToByte(hex[5..7], 16);
                TopBarForegroundColorPreview.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    global::Windows.UI.Color.FromArgb(255, r, g, b));
            }
        }
        catch
        {
            TopBarForegroundColorPreview.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                global::Windows.UI.Color.FromArgb(255, 255, 255, 255));
        }
    }

    private void OnFinderBubbleChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.FinderBubbleOpacity = e.NewValue;
        SyncLabels();
    }

    private void OnShowFinderBubbleButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.ShowFinderBubble = !_settings.ShowFinderBubble;
        SyncLabels();
        UpdateSectionUi();
    }

    private void OnFinderHotkeyButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.FinderHotkeyEnabled = !_settings.FinderHotkeyEnabled;
        UpdateSectionUi();
    }

    private void OnDiscordButtonEnabledButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.DiscordButtonEnabled = !_settings.DiscordButtonEnabled;
        UpdateSectionUi();
    }

    private void OnMusicButtonEnabledButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.MusicButtonEnabled = !_settings.MusicButtonEnabled;
        UpdateSectionUi();
    }

    private void OnMusicShowVolumeButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.MusicShowVolume = !_settings.MusicShowVolume;
        UpdateSectionUi();
    }

    private void OnMusicShowSourceToggleButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.MusicShowSourceToggle = !_settings.MusicShowSourceToggle;
        UpdateSectionUi();
    }

    private void OnHideForFullscreenButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.HideForFullscreen = !_settings.HideForFullscreen;
        UpdateSectionUi();
    }

    private void OnBackgroundOptimizationEnabledButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.BackgroundOptimizationEnabled = !_settings.BackgroundOptimizationEnabled;
        UpdateSectionUi();
    }

    private void OnAppButtonOutlineButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.ShowAppButtonOutline = !_settings.ShowAppButtonOutline;
        UpdateSectionUi();
    }

    private void OnSystemPowerBoostEnabledButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.SystemPowerBoostEnabled = !_settings.SystemPowerBoostEnabled;
        UpdateSectionUi();
    }

    private void OnQuietLaptopOutsideGamesEnabledButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.QuietLaptopOutsideGamesEnabled = !_settings.QuietLaptopOutsideGamesEnabled;
        UpdateSectionUi();
    }

    private void OnShortcutSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || sender is not ComboBox { Tag: string tag, SelectedItem: ShortcutOption option })
        {
            return;
        }

        int index = int.Parse(tag);
        var currentSetting = _settings.ShortcutButtons.ElementAtOrDefault(index);
        string displayName = currentSetting is null || string.Equals(currentSetting.DisplayName, currentSetting.AppName, StringComparison.Ordinal)
            ? option.Name
            : currentSetting.DisplayName;

        _settings.SetShortcutButton(index, string.IsNullOrWhiteSpace(option.AppId)
            ? null
            : new AppShortcutSetting(option.Name, option.AppId, displayName));
        ApplyShortcutSelections();
    }

    private void OnShortcutNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing || sender is not TextBox { Tag: string tag } textBox)
        {
            return;
        }

        int index = int.Parse(tag);
        var currentSetting = _settings.ShortcutButtons.ElementAtOrDefault(index);
        if (currentSetting is null)
        {
            return;
        }

        string displayName = string.IsNullOrWhiteSpace(textBox.Text)
            ? currentSetting.AppName
            : textBox.Text.Trim();

        var updatedSetting = currentSetting with
        {
            DisplayName = displayName
        };

        if (updatedSetting == currentSetting)
        {
            return;
        }

        _settings.SetShortcutButton(index, updatedSetting);
    }

    private void SyncLabels()
    {
        AiProviderValueText.Text = AiProviderKind.ToDisplayName(_settings.AiProvider);
        TopBarDisplayModeValueText.Text = _settings.TopBarDisplayMode;
        TopBarStyleValueText.Text = _settings.TopBarStyle == "Transparent" ? "Clear" : _settings.TopBarStyle;
        TopBarOpacityValueText.Text = $"{Math.Round(_settings.TopBarOpacity * 100):0}%";
        ClockOffsetValueText.Text = $"{_settings.ClockOffset:+#;-#;0}px";
        ClockBalanceValueText.Text = $"{Math.Round(_settings.ClockBalance * 100):0}%";
        MenuTintValueText.Text = $"{Math.Round(_settings.MenuTintOpacity * 100):0}%";
        FinderBubbleValueText.Text = $"{Math.Round(_settings.FinderBubbleOpacity * 100):0}%";
        BlurIntensityValueText.Text = $"{Math.Round(_settings.BlurIntensity * 100):0}%";
        RunCatRunnerValueText.Text = _settings.RunCatRunner;
        GameDetectionModeValueText.Text = "Hybrid";
    }

    private void LoadMonitorOptions()
    {
        List<MonitorInfo2> monitors = MonitorService.GetAllMonitors();
        List<MonitorInfo2> monitorsByPosition = monitors
            .OrderBy(static monitor => monitor.Bounds.Left)
            .ThenBy(static monitor => monitor.Bounds.Top)
            .ToList();

        bool useHorizontalLabels = GetAxisSpan(monitorsByPosition, static monitor => monitor.Bounds.Left, static monitor => monitor.Bounds.Right)
            >= GetAxisSpan(monitorsByPosition, static monitor => monitor.Bounds.Top, static monitor => monitor.Bounds.Bottom);

        _monitorOptions = monitorsByPosition
            .Select((monitor, index) => new MonitorSelectionOption(
                monitor.Id,
                CreateMonitorLabel(monitor, index, monitorsByPosition.Count, useHorizontalLabels),
                monitor.IsPrimary))
            .ToArray();

        EnsureValidTopBarMonitorSelection();
    }

    private static int GetAxisSpan(
        IReadOnlyList<MonitorInfo2> monitors,
        Func<MonitorInfo2, int> startSelector,
        Func<MonitorInfo2, int> endSelector)
    {
        if (monitors.Count == 0)
        {
            return 0;
        }

        int start = monitors.Min(startSelector);
        int end = monitors.Max(endSelector);
        return end - start;
    }

    private static string CreateMonitorLabel(MonitorInfo2 monitor, int index, int count, bool useHorizontalLabels)
    {
        string positionLabel = count switch
        {
            1 => "Only display",
            2 => useHorizontalLabels
                ? index == 0 ? "Left display" : "Right display"
                : index == 0 ? "Top display" : "Bottom display",
            3 => useHorizontalLabels
                ? index == 0 ? "Left display" : index == 1 ? "Center display" : "Right display"
                : index == 0 ? "Top display" : index == 1 ? "Center display" : "Bottom display",
            _ => $"Display {index + 1}"
        };
        int width = monitor.Bounds.Right - monitor.Bounds.Left;
        int height = monitor.Bounds.Bottom - monitor.Bounds.Top;
        string primarySuffix = monitor.IsPrimary ? " • Primary" : string.Empty;
        return $"{positionLabel}{primarySuffix} • {width}x{height}";
    }

    private void EnsureValidTopBarMonitorSelection()
    {
        if (_settings.TopBarDisplayMode != "Custom" || _monitorOptions.Count == 0)
        {
            return;
        }

        bool hasSelectedMonitor = _settings.TopBarMonitorIds.Any(id =>
            _monitorOptions.Any(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase)));
        if (hasSelectedMonitor)
        {
            return;
        }

        string fallbackMonitorId = _monitorOptions.FirstOrDefault(option =>
                string.Equals(option.Id, _preferredMonitorId, StringComparison.OrdinalIgnoreCase))?.Id
            ?? _monitorOptions.FirstOrDefault(static option => option.IsPrimary)?.Id
            ?? _monitorOptions[0].Id;
        _settings.SetTopBarMonitorIds([fallbackMonitorId]);
    }

    private void OnTopBarDisplayModeButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode })
        {
            return;
        }

        _settings.TopBarDisplayMode = mode;
        if (mode == "Custom")
        {
            LoadMonitorOptions();
        }

        SyncLabels();
        UpdateSectionUi();
    }

    private void OnTopBarMonitorToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string monitorId } || _monitorOptions.Count == 0)
        {
            return;
        }

        List<string> selectedIds = _settings.TopBarMonitorIds
            .Where(id => _monitorOptions.Any(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        bool isSelected = selectedIds.Contains(monitorId, StringComparer.OrdinalIgnoreCase);

        if (isSelected)
        {
            if (selectedIds.Count == 1)
            {
                return;
            }

            selectedIds.RemoveAll(id => string.Equals(id, monitorId, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            selectedIds.Add(monitorId);
        }

        _settings.SetTopBarMonitorIds(selectedIds);
        UpdateSectionUi();
    }

    private async Task LoadDetectedGamesAsync()
    {
        IReadOnlyList<string> gameNames = await GameDetectionService.GetCatalogGameDisplayNamesAsync();

        DispatcherQueue.TryEnqueue(() =>
        {
            DetectedGamesTextBox.Text = gameNames.Count == 0
                ? "No Windows or Xbox games were detected on this machine."
                : string.Join(Environment.NewLine, gameNames);
        });
    }

    private async Task LoadInstalledAppsAsync()
    {
        try
        {
            var apps = await InstalledAppService.GetAppsAsync();
            _shortcutOptions = [new ShortcutOption("None", string.Empty), .. apps.Select(app => new ShortcutOption(app.Name, app.AppId))];
        }
        catch
        {
            _shortcutOptions = [new ShortcutOption("None", string.Empty)];
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            _isInitializing = true;
            foreach (var comboBox in GetShortcutComboBoxes())
            {
                comboBox.ItemsSource = _shortcutOptions;
            }
            ApplyShortcutSelections();
            _isInitializing = false;
        });
    }

    private void OnRunCatEnabledButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.RunCatEnabled = !_settings.RunCatEnabled;
        SyncLabels();
        UpdateSectionUi();
    }

    private void OnRunCatRunnerButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string runner })
        {
            _settings.RunCatRunner = runner;
            SyncLabels();
            UpdateSectionUi();
        }
    }

    private void OnGameProcessesChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        IEnumerable<string> processNames = GameProcessesTextBox.Text
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(static value => value.Trim());

        _settings.SetGameProcessNames(processNames);
    }

    private void OnDoneClick(object sender, RoutedEventArgs e)
    {
        HideWindow();
    }

    private void OnSectionButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section })
        {
            _retainedVerticalOffset = 0;
            _selectedSection = section;
            UpdateSectionUi();
            ContentScrollViewer.ChangeView(null, 0, null, true);
        }
    }

    private void OnTopBarStyleButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string style })
        {
            _settings.TopBarStyle = style;
            SyncLabels();
            UpdateSectionUi();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_persistSessionStateOnClose)
        {
            CacheSessionState();
        }
        CancelSessionKeepAlive();
        _sessionKeepAliveTimer.Stop();
        _aiModelCatalogCancellationSource?.Cancel();
        _aiModelCatalogCancellationSource?.Dispose();
        _aiModelCatalogCancellationSource = null;
        SizeChanged -= OnSettingsWindowSizeChanged;
        _speechModelDownloadCancellationSource?.Cancel();
        _speechModelDownloadCancellationSource?.Dispose();
        _speechModelDownloadCancellationSource = null;
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfig = null;
        if (_finderAiWindow is not null)
        {
            _finderAiWindow.Close();
            _finderAiWindow = null;
        }
    }

    private void OnSettingsWindowSizeChanged(object sender, WindowSizeChangedEventArgs e)
    {
        UpdateContentHostWidth();
    }

    internal void HideWindow(bool keepSessionWarm = true)
    {
        _retainedVerticalOffset = ContentScrollViewer.VerticalOffset;
        CacheSessionState();
        if (keepSessionWarm)
        {
            ScheduleSessionKeepAlive();
        }
        else
        {
            CancelSessionKeepAlive();
        }

        if (_hwnd != IntPtr.Zero)
        {
            ShowWindowNative(_hwnd, SW_HIDE);
        }
    }

    internal void Destroy()
    {
        _persistSessionStateOnClose = false;
        CancelSessionKeepAlive();
        _sessionKeepAliveTimer.Stop();
        Close();
    }

    private void ApplyWindowSize()
    {
        int screenWidth = _screen.Right - _screen.Left;
        int screenHeight = _screen.Bottom - _screen.Top;

        int width = Math.Max(540, (int)Math.Round(screenWidth * 0.7));
        int height = Math.Max(420, (int)Math.Round(screenHeight * 0.7));

        PanelBorder.Width = double.NaN;
        PanelBorder.Height = double.NaN;

        WindowHelper.PositionOnMonitor(
            this,
            _screen.Left + ((screenWidth - width) / 2),
            _screen.Top + ((screenHeight - height) / 2),
            width,
            height);

        DispatcherQueue.TryEnqueue(UpdateContentHostWidth);
    }

    private void UpdateContentHostWidth()
    {
        double horizontalPadding = ContentScrollViewer.Padding.Left + ContentScrollViewer.Padding.Right;
        double availableWidth = Math.Max(0, ContentScrollViewer.ActualWidth - horizontalPadding);

        if (availableWidth <= 0)
        {
            return;
        }

        ContentHost.MinWidth = availableWidth;
    }

    private void UpdateSectionUi()
    {
        LoadMonitorOptions();
        TopBarSectionPanel.Visibility = _selectedSection == "TopBar" ? Visibility.Visible : Visibility.Collapsed;
        AiSectionPanel.Visibility = _selectedSection == "AI" ? Visibility.Visible : Visibility.Collapsed;
        DiscordSectionPanel.Visibility = _selectedSection == "Discord" ? Visibility.Visible : Visibility.Collapsed;
        MusicSectionPanel.Visibility = _selectedSection == "Music" ? Visibility.Visible : Visibility.Collapsed;
        GamesSectionPanel.Visibility = _selectedSection == "Games" ? Visibility.Visible : Visibility.Collapsed;
        MenuSectionPanel.Visibility = _selectedSection == "Menu" ? Visibility.Visible : Visibility.Collapsed;
        RunCatSectionPanel.Visibility = _selectedSection == "RunCat" ? Visibility.Visible : Visibility.Collapsed;

        UpdateSectionButton(TopBarSectionButton, _selectedSection == "TopBar");
        UpdateSectionButton(AiSectionButton, _selectedSection == "AI");
        UpdateSectionButton(DiscordSectionButton, _selectedSection == "Discord");
        UpdateSectionButton(MusicSectionButton, _selectedSection == "Music");
        UpdateSectionButton(GamesSectionButton, _selectedSection == "Games");
        UpdateSectionButton(MenuSectionButton, _selectedSection == "Menu");
        UpdateSectionButton(RunCatSectionButton, _selectedSection == "RunCat");

        UpdateTopBarStyleButton(TopBarStyleSolidButton, _settings.TopBarStyle == "Solid");
        UpdateTopBarStyleButton(TopBarStyleBlurButton, _settings.TopBarStyle == "Blur");
        UpdateTopBarStyleButton(TopBarStyleAdaptiveButton, _settings.TopBarStyle == "Adaptive");
        UpdateTopBarStyleButton(TopBarStyleTransparentButton, _settings.TopBarStyle == "Transparent");
        UpdateTopBarStyleButton(TopBarDisplayModePrimaryButton, _settings.TopBarDisplayMode == "Primary");
        UpdateTopBarStyleButton(TopBarDisplayModeAllButton, _settings.TopBarDisplayMode == "All");
        UpdateTopBarStyleButton(TopBarDisplayModeCustomButton, _settings.TopBarDisplayMode == "Custom");
        UpdateTopBarMonitorSelection();
        SolidColorPanel.Visibility = _settings.TopBarStyle == "Solid" ? Visibility.Visible : Visibility.Collapsed;
        BlurIntensityPanel.Visibility = _settings.TopBarStyle == "Blur" ? Visibility.Visible : Visibility.Collapsed;
        TopBarOpacityPanel.Visibility = _settings.TopBarStyle is "Solid" or "Blur"
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateAiProviderButtons();
        ApplyAiProviderInputs();
        SyncSolidColorPreview();
        SyncTopBarForegroundColorPreview();
        UpdateFinderBubbleButton();
        UpdateFinderHotkeyButton();
        UpdateAppButtonOutlineButton();
        UpdateDiscordButtons();
        UpdateMusicButtons();
        UpdateGameButtons();
        UpdateRunCatButtons();
    }

    private void UpdateSectionButton(Button button, bool isSelected)
    {
        UpdateToggleButton(button, isSelected, _useDarkForeground);
    }

    private void UpdateTopBarStyleButton(Button button, bool isSelected)
    {
        UpdateToggleButton(button, isSelected, _useDarkForeground);
    }

    private void UpdateTopBarMonitorSelection()
    {
        TopBarMonitorSelectionPanel.Visibility = _settings.TopBarDisplayMode == "Custom"
            ? Visibility.Visible
            : Visibility.Collapsed;
        TopBarMonitorSelectionPanel.Children.Clear();

        if (_settings.TopBarDisplayMode != "Custom")
        {
            return;
        }

        foreach (MonitorSelectionOption option in _monitorOptions)
        {
            bool isSelected = _settings.TopBarMonitorIds.Contains(option.Id, StringComparer.OrdinalIgnoreCase);
            var button = new Button
            {
                Tag = option.Id,
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                IsTabStop = false
            };
            button.Click += OnTopBarMonitorToggleClick;
            UpdateToggleButton(button, isSelected, _useDarkForeground);
            button.Content = new TextBlock
            {
                Text = option.Label,
                TextWrapping = TextWrapping.WrapWholeWords,
                FontSize = 12,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
                Foreground = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground, isSelected ? (byte)255 : (byte)214)
            };
            TopBarMonitorSelectionPanel.Children.Add(button);
        }
    }

    private void OnAiProviderButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string provider })
        {
            return;
        }

        _settings.AiProvider = provider;
        SyncLabels();
        UpdateSectionUi();
    }

    private void UpdateAiProviderButtons()
    {
        UpdateTopBarStyleButton(AiProviderChatGptButton, _settings.AiProvider == AiProviderKind.ChatGptPremium);
        UpdateTopBarStyleButton(AiProviderOpenAiButton, _settings.AiProvider == AiProviderKind.OpenAi);
        UpdateTopBarStyleButton(AiProviderAnthropicButton, _settings.AiProvider == AiProviderKind.Anthropic);
        UpdateTopBarStyleButton(AiProviderMistralButton, _settings.AiProvider == AiProviderKind.Mistral);
        UpdateTopBarStyleButton(AiProviderOllamaButton, _settings.AiProvider == AiProviderKind.Ollama);
        UpdateTopBarStyleButton(AiProviderOllamaCloudButton, _settings.AiProvider == AiProviderKind.OllamaCloud);
    }

    private void ApplyAiProviderInputs()
    {
        _isInitializing = true;

        string provider = _settings.AiProvider;
        bool isChatGpt = provider == AiProviderKind.ChatGptPremium;
        bool showsBaseUrl = provider != AiProviderKind.ChatGptPremium;
        bool showsCredential = provider is AiProviderKind.OpenAi or AiProviderKind.Anthropic or AiProviderKind.Mistral or AiProviderKind.OllamaCloud;

        AiChatGptPanel.Visibility = isChatGpt ? Visibility.Visible : Visibility.Collapsed;
        AiBaseUrlPanel.Visibility = showsBaseUrl ? Visibility.Visible : Visibility.Collapsed;
        AiCredentialPanel.Visibility = showsCredential ? Visibility.Visible : Visibility.Collapsed;
        AiModelPanel.Visibility = Visibility.Visible;

        ChatGptAuthFilePathTextBox.Text = string.IsNullOrWhiteSpace(_settings.ChatGptAuthFilePath)
            ? AiSecretStore.DetectDefaultChatGptAuthPath() ?? string.Empty
            : _settings.ChatGptAuthFilePath;
        AiBaseUrlTextBox.Text = GetCurrentAiBaseUrl();
        AiBaseUrlTextBox.PlaceholderText = GetCurrentAiBaseUrlPlaceholder();
        AiBaseUrlLabelText.Text = provider switch
        {
            AiProviderKind.Ollama => "Ollama Endpoint",
            AiProviderKind.OllamaCloud => "Ollama Cloud Base URL",
            _ => "Base URL"
        };
        AiModelLabelText.Text = isChatGpt ? "Preferred Model" : "Default Model";
        AiCredentialLabelText.Text = provider == AiProviderKind.OllamaCloud ? "Cloud API Key" : "API Key";
        AiCredentialPasswordBox.Password = string.Empty;
        AiProviderStatusText.Text = GetAiProviderStatusText(provider);
        UpdateAiHeroCard();
        UpdateAiValidationUi();
        ApplyAiSpeechModelInputs();

        _isInitializing = false;
        ApplyAiModelCatalogPlaceholder();
        _ = LoadAiModelCatalogAsync();
    }

    private void InitializeAiSpeechModelPicker()
    {
        _isInitializing = true;
        AiSpeechModelComboBox.ItemsSource = LocalSpeechModelCatalog.GetAll();
        AiSpeechModelComboBox.SelectedItem = LocalSpeechModelCatalog.GetById(_settings.LocalSpeechModelId)
            ?? LocalSpeechModelCatalog.GetDefault();
        _isInitializing = false;
        ApplyAiSpeechModelInputs();
    }

    private void ApplyAiSpeechModelInputs()
    {
        LocalSpeechModelDefinition model = LocalSpeechModelCatalog.GetById(_settings.LocalSpeechModelId)
            ?? LocalSpeechModelCatalog.GetDefault();

        _isInitializing = true;
        if (AiSpeechModelComboBox.SelectedItem is not LocalSpeechModelDefinition selectedModel ||
            !string.Equals(selectedModel.Id, model.Id, StringComparison.Ordinal))
        {
            AiSpeechModelComboBox.SelectedItem = model;
        }
        _isInitializing = false;

        bool isInstalled = _localSpeechModelStore.IsInstalled(model);
        bool isDownloadingSelectedModel = string.Equals(_activeSpeechModelDownloadId, model.Id, StringComparison.Ordinal);
        bool hasActiveDownload = !string.IsNullOrWhiteSpace(_activeSpeechModelDownloadId);
        bool hasOtherActiveDownload = hasActiveDownload && !isDownloadingSelectedModel;

        AiSpeechModelLibraryBadgeText.Text = model.IsRecommended ? "Recommended" : model.Family;
        AiSpeechModelTitleText.Text = model.DisplayName;
        AiSpeechModelDescriptionText.Text = model.Description;
        AiSpeechModelMetaText.Text = $"Family: {model.Family}  •  Size: ~{model.SizeMb} MB  •  Speed: {model.SpeedLabel}  •  Accuracy: {model.AccuracyLabel}";
        AiSpeechModelCapabilitiesText.Text = $"Languages: {model.LanguagesLabel}  •  Translation: {(model.SupportsTranslation ? "Yes" : "No")}  •  Language selection: {(model.SupportsLanguageSelection ? "Supported" : "Auto only")}";
        AiSpeechModelOutputText.Text = model.OutputStyle;
        AiSpeechModelStoragePathText.Text = $"Storage: {Path.Combine(_localSpeechModelStore.ModelsDirectoryPath, model.StorageName)}";
        AiSpeechModelStatusText.Text = GetAiSpeechModelStatusText(model, isInstalled, isDownloadingSelectedModel, hasOtherActiveDownload);
        AiSpeechModelProgressBar.Visibility = isDownloadingSelectedModel ? Visibility.Visible : Visibility.Collapsed;
        AiSpeechModelProgressBar.Value = isDownloadingSelectedModel ? Math.Clamp(_speechModelProgressPercent, 0, 100) : 0;

        AiSpeechModelPrimaryActionButton.Content = isDownloadingSelectedModel
            ? "Downloading..."
            : isInstalled
                ? "Redownload"
                : "Download";
        AiSpeechModelPrimaryActionButton.IsEnabled = !hasActiveDownload;

        AiSpeechModelSecondaryActionButton.Content = isDownloadingSelectedModel ? "Cancel" : "Delete";
        AiSpeechModelSecondaryActionButton.IsEnabled = isDownloadingSelectedModel || (isInstalled && !hasActiveDownload);
    }

    private string GetAiSpeechModelStatusText(
        LocalSpeechModelDefinition model,
        bool isInstalled,
        bool isDownloadingSelectedModel,
        bool hasOtherActiveDownload)
    {
        if (string.Equals(_speechModelMessageModelId, model.Id, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(_speechModelMessage))
        {
            return _speechModelMessage;
        }

        if (isDownloadingSelectedModel)
        {
            return "Downloading model files into Veil's local speech library.";
        }

        if (hasOtherActiveDownload)
        {
            LocalSpeechModelDefinition? activeModel = LocalSpeechModelCatalog.GetById(_activeSpeechModelDownloadId);
            return $"{activeModel?.DisplayName ?? "Another model"} is downloading right now.";
        }

        return isInstalled
            ? "Stored locally and ready for future integration inside Veil."
            : "Not downloaded yet. Veil will keep this model only in local app data.";
    }

    private void OnAiSpeechModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || AiSpeechModelComboBox.SelectedItem is not LocalSpeechModelDefinition model)
        {
            return;
        }

        _settings.LocalSpeechModelId = model.Id;
        ApplyAiSpeechModelInputs();
    }

    private async void OnAiSpeechModelPrimaryActionClick(object sender, RoutedEventArgs e)
    {
        if (AiSpeechModelComboBox.SelectedItem is not LocalSpeechModelDefinition model ||
            !string.IsNullOrWhiteSpace(_activeSpeechModelDownloadId))
        {
            return;
        }

        try
        {
            if (_localSpeechModelStore.IsInstalled(model))
            {
                _localSpeechModelStore.DeleteModel(model);
            }

            _speechModelDownloadCancellationSource?.Dispose();
            _speechModelDownloadCancellationSource = new CancellationTokenSource();
            _activeSpeechModelDownloadId = model.Id;
            _speechModelMessageModelId = model.Id;
            _speechModelMessage = $"Starting download for {model.DisplayName}.";
            _speechModelProgressPercent = 0;
            ApplyAiSpeechModelInputs();

            var progress = new Progress<LocalSpeechModelDownloadProgress>(update =>
            {
                if (!string.Equals(update.ModelId, model.Id, StringComparison.Ordinal))
                {
                    return;
                }

                switch (update.Stage)
                {
                    case LocalSpeechModelDownloadStage.Downloading:
                        _speechModelProgressPercent = update.TotalBytes > 0
                            ? (update.DownloadedBytes * 100d) / update.TotalBytes
                            : 0;
                        _speechModelMessage = $"Downloading {model.DisplayName}: {FormatMegabytes(update.DownloadedBytes)} / {(update.TotalBytes > 0 ? FormatMegabytes(update.TotalBytes) : $"~{model.SizeMb} MB")}.";
                        break;
                    case LocalSpeechModelDownloadStage.Verifying:
                        _speechModelProgressPercent = 100;
                        _speechModelMessage = $"Verifying checksum for {model.DisplayName}.";
                        break;
                    case LocalSpeechModelDownloadStage.Extracting:
                        _speechModelProgressPercent = 100;
                        _speechModelMessage = $"Extracting {model.DisplayName} into Veil's local speech library.";
                        break;
                    case LocalSpeechModelDownloadStage.Completed:
                        _speechModelProgressPercent = 100;
                        _speechModelMessage = $"{model.DisplayName} was downloaded and stored locally.";
                        break;
                }

                _speechModelMessageModelId = model.Id;
                ApplyAiSpeechModelInputs();
            });

            await _localSpeechModelStore.DownloadModelAsync(
                model,
                progress,
                _speechModelDownloadCancellationSource.Token);

            _speechModelMessageModelId = model.Id;
            _speechModelMessage = $"{model.DisplayName} is ready for future local use in Veil.";
            _speechModelProgressPercent = 100;
        }
        catch (OperationCanceledException)
        {
            _speechModelMessageModelId = model.Id;
            _speechModelMessage = $"{model.DisplayName} download was cancelled.";
            _speechModelProgressPercent = 0;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to download speech model {model.Id}.", ex);
            _speechModelMessageModelId = model.Id;
            _speechModelMessage = $"Download failed: {ex.Message}";
            _speechModelProgressPercent = 0;
        }
        finally
        {
            _activeSpeechModelDownloadId = null;
            _speechModelDownloadCancellationSource?.Dispose();
            _speechModelDownloadCancellationSource = null;
            ApplyAiSpeechModelInputs();
        }
    }

    private void OnAiSpeechModelSecondaryActionClick(object sender, RoutedEventArgs e)
    {
        if (AiSpeechModelComboBox.SelectedItem is not LocalSpeechModelDefinition model)
        {
            return;
        }

        if (string.Equals(_activeSpeechModelDownloadId, model.Id, StringComparison.Ordinal))
        {
            _speechModelDownloadCancellationSource?.Cancel();
            return;
        }

        try
        {
            _localSpeechModelStore.DeleteModel(model);
            _speechModelMessageModelId = model.Id;
            _speechModelMessage = $"{model.DisplayName} was removed from Veil's local storage.";
            _speechModelProgressPercent = 0;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to delete speech model {model.Id}.", ex);
            _speechModelMessageModelId = model.Id;
            _speechModelMessage = $"Delete failed: {ex.Message}";
        }

        ApplyAiSpeechModelInputs();
    }

    private static string FormatMegabytes(long bytes)
    {
        double value = bytes / (1024d * 1024d);
        return $"{Math.Max(0, value):0} MB";
    }

    private void OnValidateAiProviderClick(object sender, RoutedEventArgs e)
    {
        UpdateAiValidationUi();
    }

    private void OnOpenAiWindowClick(object sender, RoutedEventArgs e)
    {
        if (_finderAiWindow is null)
        {
            _finderAiWindow = new FinderAiWindow(_screen);
            _finderAiWindow.Closed += OnFinderAiWindowClosed;
        }

        _finderAiWindow.ShowCentered();
    }

    private void OnFinderAiWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is FinderAiWindow aiWindow)
        {
            aiWindow.Closed -= OnFinderAiWindowClosed;
            if (ReferenceEquals(_finderAiWindow, aiWindow))
            {
                _finderAiWindow = null;
            }
        }
    }

    private void OnChatGptAuthFilePathChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.ChatGptAuthFilePath = ChatGptAuthFilePathTextBox.Text;
    }

    private void OnUseDetectedChatGptAuthPathClick(object sender, RoutedEventArgs e)
    {
        string? detectedPath = AiSecretStore.DetectDefaultChatGptAuthPath();
        if (string.IsNullOrWhiteSpace(detectedPath))
        {
            AiProviderStatusText.Text = "No local Codex or ChatGPT auth file was detected on this machine.";
            return;
        }

        _settings.ChatGptAuthFilePath = detectedPath;
        _isInitializing = true;
        ChatGptAuthFilePathTextBox.Text = detectedPath;
        _isInitializing = false;
        UpdateSectionUi();
    }

    private void OnImportChatGptAuthClick(object sender, RoutedEventArgs e)
    {
        string authFilePath = ChatGptAuthFilePathTextBox.Text.Trim();
        if (!_aiSecretStore.TryImportChatGptAuth(authFilePath, out string errorMessage))
        {
            AiProviderStatusText.Text = errorMessage;
            return;
        }

        _settings.ChatGptAuthFilePath = authFilePath;
        UpdateSectionUi();
    }

    private void OnClearChatGptAuthClick(object sender, RoutedEventArgs e)
    {
        _aiSecretStore.DeleteSecret(AiSecretNames.ChatGptOAuthPayload);
        UpdateSectionUi();
    }

    private void OnAiBaseUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        SetCurrentAiBaseUrl(AiBaseUrlTextBox.Text);
    }

    private void OnAiModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || AiModelComboBox.SelectedItem is not AiModelCatalogEntry entry)
        {
            return;
        }

        SetCurrentAiModel(entry.ModelId);
        UpdateAiHeroCard();
        UpdateAiValidationUi();
    }

    private async void OnRefreshAiModelCatalogClick(object sender, RoutedEventArgs e)
    {
        await LoadAiModelCatalogAsync(forceRefresh: true);
    }

    private void OnSaveAiCredentialClick(object sender, RoutedEventArgs e)
    {
        string? secretName = GetCurrentAiSecretName();
        if (string.IsNullOrWhiteSpace(secretName))
        {
            AiProviderStatusText.Text = "This provider does not need a separate API secret in Veil.";
            return;
        }

        string secretValue = AiCredentialPasswordBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(secretValue))
        {
            AiProviderStatusText.Text = "Paste a credential before saving it.";
            return;
        }

        _aiSecretStore.SaveSecret(secretName, secretValue);
        AiCredentialPasswordBox.Password = string.Empty;
        UpdateSectionUi();
    }

    private void OnClearAiCredentialClick(object sender, RoutedEventArgs e)
    {
        string? secretName = GetCurrentAiSecretName();
        if (!string.IsNullOrWhiteSpace(secretName))
        {
            _aiSecretStore.DeleteSecret(secretName);
        }

        AiCredentialPasswordBox.Password = string.Empty;
        UpdateSectionUi();
    }

    private string GetCurrentAiBaseUrl()
    {
        return _settings.AiProvider switch
        {
            AiProviderKind.OpenAi => _settings.OpenAiBaseUrl,
            AiProviderKind.Anthropic => _settings.AnthropicBaseUrl,
            AiProviderKind.Mistral => _settings.MistralBaseUrl,
            AiProviderKind.Ollama => _settings.OllamaBaseUrl,
            AiProviderKind.OllamaCloud => _settings.OllamaCloudBaseUrl,
            _ => string.Empty
        };
    }

    private string GetCurrentAiBaseUrlPlaceholder()
    {
        return _settings.AiProvider switch
        {
            AiProviderKind.OpenAi => "https://api.openai.com/v1",
            AiProviderKind.Anthropic => "https://api.anthropic.com",
            AiProviderKind.Mistral => "https://api.mistral.ai/v1",
            AiProviderKind.Ollama => "http://127.0.0.1:11434/api",
            AiProviderKind.OllamaCloud => "https://ollama.com/api",
            _ => string.Empty
        };
    }

    private string GetCurrentAiModel()
    {
        return _settings.AiProvider switch
        {
            AiProviderKind.ChatGptPremium => _settings.ChatGptModel,
            AiProviderKind.OpenAi => _settings.OpenAiModel,
            AiProviderKind.Anthropic => _settings.AnthropicModel,
            AiProviderKind.Mistral => _settings.MistralModel,
            AiProviderKind.Ollama => _settings.OllamaModel,
            AiProviderKind.OllamaCloud => _settings.OllamaCloudModel,
            _ => string.Empty
        };
    }

    private void ApplyAiModelCatalogPlaceholder()
    {
        string currentModel = GetCurrentAiModel();
        _aiModelOptions = CreateAiModelOptions([], currentModel);

        _isInitializing = true;
        AiModelComboBox.ItemsSource = _aiModelOptions;
        AiModelComboBox.SelectedItem = _aiModelOptions.FirstOrDefault(item =>
            string.Equals(item.ModelId, currentModel, StringComparison.Ordinal));
        AiModelCatalogStatusText.Text = $"Loading models.dev catalog for {AiProviderKind.ToDisplayName(_settings.AiProvider)}.";
        _isInitializing = false;
    }

    private async Task LoadAiModelCatalogAsync(bool forceRefresh = false)
    {
        _aiModelCatalogCancellationSource?.Cancel();
        _aiModelCatalogCancellationSource?.Dispose();
        _aiModelCatalogCancellationSource = new CancellationTokenSource();
        CancellationToken cancellationToken = _aiModelCatalogCancellationSource.Token;
        string currentModel = GetCurrentAiModel();

        _isInitializing = true;
        AiModelRefreshButton.IsEnabled = false;
        AiModelCatalogStatusText.Text = forceRefresh
            ? "Refreshing models.dev catalog..."
            : $"Loading models.dev catalog for {AiProviderKind.ToDisplayName(_settings.AiProvider)}.";
        _isInitializing = false;

        try
        {
            AiModelCatalogSnapshot snapshot = await _aiModelCatalogService.GetModelsForProviderAsync(
                _settings.AiProvider,
                forceRefresh,
                cancellationToken);

            _aiModelOptions = CreateAiModelOptions(snapshot.Models, currentModel);
            _isInitializing = true;
            AiModelComboBox.ItemsSource = _aiModelOptions;
            AiModelComboBox.SelectedItem = _aiModelOptions.FirstOrDefault(item =>
                string.Equals(item.ModelId, currentModel, StringComparison.Ordinal));
            AiModelCatalogStatusText.Text = snapshot.StatusText;
            _isInitializing = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to load AI model catalog.", ex);
            _aiModelOptions = CreateAiModelOptions([], currentModel);
            _isInitializing = true;
            AiModelComboBox.ItemsSource = _aiModelOptions;
            AiModelComboBox.SelectedItem = _aiModelOptions.FirstOrDefault(item =>
                string.Equals(item.ModelId, currentModel, StringComparison.Ordinal));
            AiModelCatalogStatusText.Text = $"Model catalog unavailable: {ex.Message}";
            _isInitializing = false;
        }
        finally
        {
            AiModelRefreshButton.IsEnabled = true;
        }
    }

    private static IReadOnlyList<AiModelCatalogEntry> CreateAiModelOptions(
        IReadOnlyList<AiModelCatalogEntry> catalog,
        string currentModel)
    {
        var options = new List<AiModelCatalogEntry>();

        if (!string.IsNullOrWhiteSpace(currentModel))
        {
            options.Add(new AiModelCatalogEntry(
                string.Empty,
                string.Empty,
                currentModel,
                currentModel,
                $"{currentModel}  •  current",
                "current"));
        }

        options.AddRange(catalog);
        return options
            .GroupBy(static item => item.ModelId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(item => string.Equals(item.ModelId, currentModel, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(static item => item.DisplayLabel, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string GetCurrentAiModelPlaceholder()
    {
        return _settings.AiProvider switch
        {
            AiProviderKind.ChatGptPremium => "gpt-5.4",
            AiProviderKind.OpenAi => "gpt-5.4",
            AiProviderKind.Anthropic => "claude-sonnet-4-20250514",
            AiProviderKind.Mistral => "mistral-large-latest",
            AiProviderKind.Ollama => "qwen3-coder",
            AiProviderKind.OllamaCloud => "gpt-oss:120b",
            _ => string.Empty
        };
    }

    private void SetCurrentAiBaseUrl(string value)
    {
        switch (_settings.AiProvider)
        {
            case AiProviderKind.OpenAi:
                _settings.OpenAiBaseUrl = value;
                break;
            case AiProviderKind.Anthropic:
                _settings.AnthropicBaseUrl = value;
                break;
            case AiProviderKind.Mistral:
                _settings.MistralBaseUrl = value;
                break;
            case AiProviderKind.Ollama:
                _settings.OllamaBaseUrl = value;
                break;
            case AiProviderKind.OllamaCloud:
                _settings.OllamaCloudBaseUrl = value;
                break;
        }
    }

    private void SetCurrentAiModel(string value)
    {
        switch (_settings.AiProvider)
        {
            case AiProviderKind.ChatGptPremium:
                _settings.ChatGptModel = value;
                break;
            case AiProviderKind.OpenAi:
                _settings.OpenAiModel = value;
                break;
            case AiProviderKind.Anthropic:
                _settings.AnthropicModel = value;
                break;
            case AiProviderKind.Mistral:
                _settings.MistralModel = value;
                break;
            case AiProviderKind.Ollama:
                _settings.OllamaModel = value;
                break;
            case AiProviderKind.OllamaCloud:
                _settings.OllamaCloudModel = value;
                break;
        }
    }

    private string? GetCurrentAiSecretName()
    {
        return _settings.AiProvider switch
        {
            AiProviderKind.OpenAi => AiSecretNames.OpenAiApiKey,
            AiProviderKind.Anthropic => AiSecretNames.AnthropicApiKey,
            AiProviderKind.Mistral => AiSecretNames.MistralApiKey,
            AiProviderKind.OllamaCloud => AiSecretNames.OllamaCloudApiKey,
            _ => null
        };
    }

    private string GetAiProviderStatusText(string provider)
    {
        return provider switch
        {
            AiProviderKind.ChatGptPremium => _aiSecretStore.HasSecret(AiSecretNames.ChatGptOAuthPayload)
                ? "Local OAuth payload imported and encrypted for the current Windows account."
                : string.IsNullOrWhiteSpace(AiSecretStore.DetectDefaultChatGptAuthPath())
                    ? "No local Codex or ChatGPT auth file detected yet."
                    : "A local Codex or ChatGPT auth file is available and can be imported into Veil.",
            AiProviderKind.OpenAi => _aiSecretStore.HasSecret(AiSecretNames.OpenAiApiKey)
                ? "OpenAI API key is stored locally in encrypted form."
                : "OpenAI API key has not been stored yet.",
            AiProviderKind.Anthropic => _aiSecretStore.HasSecret(AiSecretNames.AnthropicApiKey)
                ? "Anthropic API key is stored locally in encrypted form."
                : "Anthropic API key has not been stored yet.",
            AiProviderKind.Mistral => _aiSecretStore.HasSecret(AiSecretNames.MistralApiKey)
                ? "Mistral API key is stored locally in encrypted form."
                : "Mistral API key has not been stored yet.",
            AiProviderKind.Ollama => "Ollama runs locally by default, so Veil only needs an endpoint and a model.",
            AiProviderKind.OllamaCloud => _aiSecretStore.HasSecret(AiSecretNames.OllamaCloudApiKey)
                ? "Ollama Cloud API key is stored locally in encrypted form."
                : "Ollama Cloud API key has not been stored yet.",
            _ => "Provider not configured."
        };
    }

    private void UpdateAiValidationUi()
    {
        AiProviderValidationResult result = AiProviderValidationService.Validate(_settings, _aiSecretStore);

        AiValidationSummaryText.Text = result.Summary;
        UpdateAiHeroCard(result);
        AiValidationMessagePanel.Children.Clear();

        foreach (AiProviderValidationMessage message in result.Messages)
        {
            var rowBorder = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 10, 12, 10),
                Background = new SolidColorBrush(message.IsValid
                    ? global::Windows.UI.Color.FromArgb(22, 118, 255, 168)
                    : global::Windows.UI.Color.FromArgb(20, 255, 143, 143))
            };

            var rowStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10
            };

            rowStack.Children.Add(new TextBlock
            {
                Text = message.IsValid ? "OK" : "Fix",
                FontSize = 11,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
                Foreground = new SolidColorBrush(message.IsValid
                    ? global::Windows.UI.Color.FromArgb(255, 162, 255, 211)
                    : global::Windows.UI.Color.FromArgb(255, 255, 190, 190)),
                VerticalAlignment = VerticalAlignment.Center
            });

            rowStack.Children.Add(new TextBlock
            {
                Text = message.Text,
                TextWrapping = TextWrapping.WrapWholeWords,
                FontSize = 11,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
                Foreground = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground, 220),
                VerticalAlignment = VerticalAlignment.Center
            });

            rowBorder.Child = rowStack;
            AiValidationMessagePanel.Children.Add(rowBorder);
        }

        switch (result.State)
        {
            case AiProviderValidationState.Valid:
                AiValidationBadgeBorder.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(32, 96, 220, 160));
                AiValidationBadgeText.Text = "Ready";
                AiValidationBadgeText.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 198, 255, 227));
                break;
            case AiProviderValidationState.Warning:
                AiValidationBadgeBorder.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(32, 255, 196, 92));
                AiValidationBadgeText.Text = "Review";
                AiValidationBadgeText.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 231, 184));
                break;
            default:
                AiValidationBadgeBorder.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(32, 255, 120, 120));
                AiValidationBadgeText.Text = "Not Ready";
                AiValidationBadgeText.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 214, 214));
                break;
        }
    }

    private void UpdateAiHeroCard(AiProviderValidationResult? validation = null)
    {
        string providerDisplayName = AiProviderKind.ToDisplayName(_settings.AiProvider);
        string model = GetCurrentAiModel();

        AiHeroProviderText.Text = providerDisplayName;
        AiHeroModelText.Text = string.IsNullOrWhiteSpace(model) ? "No model selected" : model.Trim();

        string runtimeTitle;
        string runtimeSubtitle;

        if (_settings.AiProvider == AiProviderKind.ChatGptPremium)
        {
            bool hasImportedAuth = _aiSecretStore.HasSecret(AiSecretNames.ChatGptOAuthPayload);
            bool hasDetectedAuth = !string.IsNullOrWhiteSpace(_settings.ChatGptAuthFilePath)
                || !string.IsNullOrWhiteSpace(AiSecretStore.DetectDefaultChatGptAuthPath());

            runtimeTitle = hasImportedAuth
                ? "Encrypted local bridge"
                : hasDetectedAuth
                    ? "Detected local bridge"
                    : "Bridge not ready";
            runtimeSubtitle = hasImportedAuth
                ? "Veil can launch OpenAI OAuth from its encrypted local copy."
                : hasDetectedAuth
                    ? "A local Codex or ChatGPT auth file is available for direct use."
                    : "Import or detect a local auth file to enable the native bridge.";
        }
        else if (_settings.AiProvider == AiProviderKind.Ollama)
        {
            runtimeTitle = "Local endpoint";
            runtimeSubtitle = "Requests stay on your configured Ollama host.";
        }
        else
        {
            bool hasSecret = !string.IsNullOrWhiteSpace(GetCurrentAiSecretName()) &&
                _aiSecretStore.HasSecret(GetCurrentAiSecretName()!);
            runtimeTitle = hasSecret ? "Encrypted API secret" : "Credential required";
            runtimeSubtitle = hasSecret
                ? "Veil stores the provider secret in the Windows user vault."
                : "Save a provider secret to complete the route.";
        }

        if (validation is { State: AiProviderValidationState.Valid })
        {
            runtimeTitle = "Ready";
        }

        AiHeroRuntimeText.Text = runtimeTitle;
        AiHeroRuntimeSubtext.Text = validation?.Summary ?? runtimeSubtitle;
    }

    private static void UpdateToggleButton(Button button, bool isSelected, bool useDarkForeground)
    {
        button.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(isSelected ? (byte)48 : (byte)0, 255, 255, 255));
        button.Foreground = ReadableSurfaceHelper.CreateTextBrush(
            useDarkForeground,
            isSelected ? (byte)255 : (byte)214);
    }

    private void UpdateFinderBubbleButton()
    {
        bool isEnabled = _settings.ShowFinderBubble;
        ShowFinderBubbleButton.Content = isEnabled ? "Enabled" : "Hidden";
        ShowFinderBubbleButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(isEnabled ? (byte)48 : (byte)0, 255, 255, 255));
        ShowFinderBubbleButton.Foreground = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground, isEnabled ? (byte)255 : (byte)214);
    }

    private void UpdateFinderHotkeyButton()
    {
        bool isEnabled = _settings.FinderHotkeyEnabled;
        FinderHotkeyButton.Content = isEnabled ? "Enabled" : "Disabled";
        FinderHotkeyButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(isEnabled ? (byte)48 : (byte)0, 255, 255, 255));
        FinderHotkeyButton.Foreground = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground, isEnabled ? (byte)255 : (byte)214);
    }

    private void UpdateAppButtonOutlineButton()
    {
        bool isEnabled = _settings.ShowAppButtonOutline;
        AppButtonOutlineButton.Content = isEnabled ? "Enabled" : "Hidden";
        AppButtonOutlineButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(isEnabled ? (byte)48 : (byte)0, 255, 255, 255));
        AppButtonOutlineButton.Foreground = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground, isEnabled ? (byte)255 : (byte)214);
    }

    private void UpdateRunCatButtons()
    {
        bool isEnabled = _settings.RunCatEnabled;
        RunCatEnabledButton.Content = isEnabled ? "Enabled" : "Disabled";
        RunCatEnabledButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(isEnabled ? (byte)48 : (byte)0, 255, 255, 255));
        RunCatEnabledButton.Foreground = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground, isEnabled ? (byte)255 : (byte)214);

        UpdateTopBarStyleButton(RunCatCatButton, _settings.RunCatRunner == "Cat");
        UpdateTopBarStyleButton(RunCatParrotButton, _settings.RunCatRunner == "Parrot");
        UpdateTopBarStyleButton(RunCatHorseButton, _settings.RunCatRunner == "Horse");
    }

    private void UpdateDiscordButtons()
    {
        bool isDiscordButtonEnabled = _settings.DiscordButtonEnabled;
        DiscordButtonEnabledButton.Content = isDiscordButtonEnabled ? "Enabled" : "Disabled";
        DiscordButtonEnabledButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(isDiscordButtonEnabled ? (byte)48 : (byte)0, 255, 255, 255));
        DiscordButtonEnabledButton.Foreground = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground, isDiscordButtonEnabled ? (byte)255 : (byte)214);
    }

    private void UpdateMusicButtons()
    {
        bool isMusicButtonEnabled = _settings.MusicButtonEnabled;
        MusicButtonEnabledButton.Content = isMusicButtonEnabled ? "Enabled" : "Disabled";
        MusicButtonEnabledButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(isMusicButtonEnabled ? (byte)48 : (byte)0, 255, 255, 255));
        MusicButtonEnabledButton.Foreground = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground, isMusicButtonEnabled ? (byte)255 : (byte)214);

        bool showVolume = _settings.MusicShowVolume;
        MusicShowVolumeButton.Content = showVolume ? "Enabled" : "Hidden";
        MusicShowVolumeButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(showVolume ? (byte)48 : (byte)0, 255, 255, 255));
        MusicShowVolumeButton.Foreground = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground, showVolume ? (byte)255 : (byte)214);

        bool showSourceToggle = _settings.MusicShowSourceToggle;
        MusicShowSourceToggleButton.Content = showSourceToggle ? "Enabled" : "Hidden";
        MusicShowSourceToggleButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(showSourceToggle ? (byte)48 : (byte)0, 255, 255, 255));
        MusicShowSourceToggleButton.Foreground = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground, showSourceToggle ? (byte)255 : (byte)214);
    }


    private void UpdateGameButtons()
    {
        bool isHideForFullscreen = _settings.HideForFullscreen;
        HideForFullscreenButton.Content = isHideForFullscreen ? "Enabled" : "Disabled";
        HideForFullscreenButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(isHideForFullscreen ? (byte)48 : (byte)0, 255, 255, 255));
        HideForFullscreenButton.Foreground = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground, isHideForFullscreen ? (byte)255 : (byte)214);

        bool isBackgroundOptimizationEnabled = _settings.BackgroundOptimizationEnabled;
        BackgroundOptimizationEnabledButton.Content = isBackgroundOptimizationEnabled ? "Enabled" : "Disabled";
        BackgroundOptimizationEnabledButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(isBackgroundOptimizationEnabled ? (byte)48 : (byte)0, 255, 255, 255));
        BackgroundOptimizationEnabledButton.Foreground = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground, isBackgroundOptimizationEnabled ? (byte)255 : (byte)214);

        bool isSystemPowerBoostEnabled = _settings.SystemPowerBoostEnabled;
        SystemPowerBoostEnabledButton.Content = isSystemPowerBoostEnabled ? "Enabled" : "Disabled";
        SystemPowerBoostEnabledButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(isSystemPowerBoostEnabled ? (byte)48 : (byte)0, 255, 255, 255));
        SystemPowerBoostEnabledButton.Foreground = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground, isSystemPowerBoostEnabled ? (byte)255 : (byte)214);

        bool isQuietLaptopOutsideGamesEnabled = _settings.QuietLaptopOutsideGamesEnabled;
        QuietLaptopOutsideGamesEnabledButton.Content = isQuietLaptopOutsideGamesEnabled ? "Enabled" : "Disabled";
        QuietLaptopOutsideGamesEnabledButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(isQuietLaptopOutsideGamesEnabled ? (byte)48 : (byte)0, 255, 255, 255));
        QuietLaptopOutsideGamesEnabledButton.Foreground = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground, isQuietLaptopOutsideGamesEnabled ? (byte)255 : (byte)214);
    }

    private void ApplyReadableContrast()
    {
        _useDarkForeground = false;
        ReadableSurfaceHelper.ApplyTextContrast(PanelBorder, _useDarkForeground);
        UpdateSectionUi();
    }

    private void ApplyShortcutSelections()
    {
        var combos = GetShortcutComboBoxes();
        var textBoxes = GetShortcutNameTextBoxes();

        for (int i = 0; i < combos.Length; i++)
        {
            var shortcut = _settings.ShortcutButtons.ElementAtOrDefault(i);
            combos[i].SelectedItem = _shortcutOptions.FirstOrDefault(option =>
                !string.IsNullOrWhiteSpace(option.AppId) &&
                shortcut is not null &&
                string.Equals(option.AppId, shortcut.AppId, StringComparison.OrdinalIgnoreCase))
                ?? _shortcutOptions[0];

            textBoxes[i].Text = shortcut?.DisplayName ?? string.Empty;
            textBoxes[i].IsEnabled = shortcut is not null;
        }
    }

    private ComboBox[] GetShortcutComboBoxes()
    {
        return
        [
            ShortcutSlot1ComboBox,
            ShortcutSlot2ComboBox,
            ShortcutSlot3ComboBox,
            ShortcutSlot4ComboBox
        ];
    }

    private TextBox[] GetShortcutNameTextBoxes()
    {
        return
        [
            ShortcutSlot1NameTextBox,
            ShortcutSlot2NameTextBox,
            ShortcutSlot3NameTextBox,
            ShortcutSlot4NameTextBox
        ];
    }

    private static bool TryGetCommittedHexColor(string? value, out string normalizedColor)
    {
        normalizedColor = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed.Length is not 6 and not 7)
        {
            return false;
        }

        return AppSettings.TryNormalizeHexColor(trimmed, out normalizedColor);
    }

    private void ScheduleSessionKeepAlive()
    {
        _sessionKeepAliveTimer.Stop();
        _sessionKeepAliveTimer.Interval = SessionKeepAliveDuration;
        _sessionKeepAliveTimer.Start();
    }

    private void CancelSessionKeepAlive()
    {
        _sessionKeepAliveTimer.Stop();
    }

    private bool ShouldResumeSession()
    {
        return TryGetCachedSessionSnapshot(out _);
    }

    private void OnSessionKeepAliveElapsed(object? sender, object e)
    {
        _sessionKeepAliveTimer.Stop();
        SessionExpired?.Invoke(this, EventArgs.Empty);
        Destroy();
    }

    private void CacheSessionState()
    {
        s_lastSessionSnapshot = new SettingsSessionSnapshot(
            _selectedSection,
            _retainedVerticalOffset,
            DateTime.UtcNow);
    }

    private void RestoreCachedSessionState()
    {
        if (!TryGetCachedSessionSnapshot(out SettingsSessionSnapshot snapshot))
        {
            ClearRetainedSessionState();
            return;
        }

        _selectedSection = snapshot.SelectedSection;
        _retainedVerticalOffset = snapshot.VerticalOffset;
        UpdateSectionUi();
    }

    private void RestoreRetainedViewport()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ContentScrollViewer.ChangeView(null, _retainedVerticalOffset, null, true);
        });
    }

    private void ClearRetainedSessionState()
    {
        _selectedSection = "TopBar";
        _retainedVerticalOffset = 0;
        s_lastSessionSnapshot = null;
    }

    private static bool TryGetCachedSessionSnapshot(out SettingsSessionSnapshot snapshot)
    {
        if (s_lastSessionSnapshot is SettingsSessionSnapshot cachedSnapshot &&
            DateTime.UtcNow - cachedSnapshot.CapturedAtUtc <= SessionKeepAliveDuration)
        {
            snapshot = cachedSnapshot;
            return true;
        }

        s_lastSessionSnapshot = null;
        snapshot = default;
        return false;
    }

    private readonly record struct SettingsSessionSnapshot(
        string SelectedSection,
        double VerticalOffset,
        DateTime CapturedAtUtc);
}
