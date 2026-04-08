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
    private readonly ScreenBounds _screen;
    private readonly string _preferredMonitorId;
    private readonly AppSettings _settings;
    private IntPtr _hwnd;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private bool _isInitializing;
    private bool _showRequested;
    private DateTime _openedAtUtc;
    private string _selectedSection = "TopBar";
    private bool _useDarkForeground;
    private IReadOnlyList<ShortcutOption> _shortcutOptions =
    [
        new ShortcutOption("None", string.Empty)
    ];
    private IReadOnlyList<MonitorSelectionOption> _monitorOptions = [];

    internal SettingsWindow(ScreenBounds screen, string preferredMonitorId)
    {
        _screen = screen;
        _preferredMonitorId = preferredMonitorId;
        _settings = AppSettings.Current;

        InitializeComponent();
        Title = "Veil Settings";

        Activated += OnFirstActivated;
        Activated += OnWindowActivated;
        Closed += OnClosed;
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
            TintColor = global::Windows.UI.Color.FromArgb(238, 26, 26, 30),
            TintOpacity = 0.2f,
            LuminosityOpacity = 0.09f,
            FallbackColor = global::Windows.UI.Color.FromArgb(194, 20, 20, 24)
        };

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark
        };

        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
    }

    private void ConfigureWindowChrome()
    {
        WindowHelper.RemoveTitleBar(this);

        int rounded = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref rounded, sizeof(int));
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated) return;
        if ((DateTime.UtcNow - _openedAtUtc).TotalMilliseconds < 300) return;
        ShowWindowNative(_hwnd, SW_HIDE);
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
        WeatherPrimaryCityTextBox.Text = _settings.WeatherPrimaryCity;
        WeatherSecondaryCitiesTextBox.Text = string.Join(Environment.NewLine, _settings.WeatherSecondaryCities);

        SyncLabels();
        UpdateSectionUi();
        ApplyShortcutSelections();
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
        _openedAtUtc = DateTime.UtcNow;
        ApplyWindowSize();
        Activate();
        SetForegroundWindow(WindowHelper.GetHwnd(this));
        ApplyReadableContrast();
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

    private void OnWeatherButtonEnabledButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.WeatherButtonEnabled = !_settings.WeatherButtonEnabled;
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

    private void OnWeatherPrimaryCityChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.WeatherPrimaryCity = WeatherPrimaryCityTextBox.Text;
    }

    private void OnWeatherSecondaryCitiesChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        IEnumerable<string> cityNames = WeatherSecondaryCitiesTextBox.Text
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(static value => value.Trim());
        _settings.SetWeatherSecondaryCities(cityNames);
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
        Close();
    }

    private void OnSectionButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section })
        {
            _selectedSection = section;
            UpdateSectionUi();
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
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfig = null;
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
    }

    private void UpdateSectionUi()
    {
        LoadMonitorOptions();
        TopBarSectionPanel.Visibility = _selectedSection == "TopBar" ? Visibility.Visible : Visibility.Collapsed;
        DiscordSectionPanel.Visibility = _selectedSection == "Discord" ? Visibility.Visible : Visibility.Collapsed;
        MusicSectionPanel.Visibility = _selectedSection == "Music" ? Visibility.Visible : Visibility.Collapsed;
        WeatherSectionPanel.Visibility = _selectedSection == "Weather" ? Visibility.Visible : Visibility.Collapsed;
        GamesSectionPanel.Visibility = _selectedSection == "Games" ? Visibility.Visible : Visibility.Collapsed;
        MenuSectionPanel.Visibility = _selectedSection == "Menu" ? Visibility.Visible : Visibility.Collapsed;
        RunCatSectionPanel.Visibility = _selectedSection == "RunCat" ? Visibility.Visible : Visibility.Collapsed;

        UpdateSectionButton(TopBarSectionButton, _selectedSection == "TopBar");
        UpdateSectionButton(DiscordSectionButton, _selectedSection == "Discord");
        UpdateSectionButton(MusicSectionButton, _selectedSection == "Music");
        UpdateSectionButton(WeatherSectionButton, _selectedSection == "Weather");
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
        SyncSolidColorPreview();
        SyncTopBarForegroundColorPreview();
        UpdateFinderBubbleButton();
        UpdateFinderHotkeyButton();
        UpdateAppButtonOutlineButton();
        UpdateDiscordButtons();
        UpdateMusicButtons();
        UpdateWeatherButtons();
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

    private void UpdateWeatherButtons()
    {
        bool isWeatherEnabled = _settings.WeatherButtonEnabled;
        WeatherButtonEnabledButton.Content = isWeatherEnabled ? "Enabled" : "Disabled";
        WeatherButtonEnabledButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(isWeatherEnabled ? (byte)48 : (byte)0, 255, 255, 255));
        WeatherButtonEnabledButton.Foreground = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground, isWeatherEnabled ? (byte)255 : (byte)214);
    }

    private void UpdateGameButtons()
    {
        bool isBackgroundOptimizationEnabled = _settings.BackgroundOptimizationEnabled;
        BackgroundOptimizationEnabledButton.Content = isBackgroundOptimizationEnabled ? "Enabled" : "Disabled";
        BackgroundOptimizationEnabledButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(isBackgroundOptimizationEnabled ? (byte)48 : (byte)0, 255, 255, 255));
        BackgroundOptimizationEnabledButton.Foreground = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground, isBackgroundOptimizationEnabled ? (byte)255 : (byte)214);

        bool isSystemPowerBoostEnabled = _settings.SystemPowerBoostEnabled;
        SystemPowerBoostEnabledButton.Content = isSystemPowerBoostEnabled ? "Enabled" : "Disabled";
        SystemPowerBoostEnabledButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(isSystemPowerBoostEnabled ? (byte)48 : (byte)0, 255, 255, 255));
        SystemPowerBoostEnabledButton.Foreground = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground, isSystemPowerBoostEnabled ? (byte)255 : (byte)214);
    }

    private void ApplyReadableContrast()
    {
        if (!GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        _useDarkForeground = ReadableSurfaceHelper.ShouldUseDarkForeground(
            rect.Left,
            rect.Top,
            Math.Max(1, rect.Right - rect.Left),
            Math.Max(1, rect.Bottom - rect.Top));

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
}
