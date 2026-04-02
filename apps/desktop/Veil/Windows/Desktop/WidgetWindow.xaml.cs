using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT;
using Veil.Configuration;
using Veil.Diagnostics;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

internal sealed record DesktopWidgetSelectionOption(string Id, string Label);

public sealed partial class WidgetWindow : Window
{
    private readonly ScreenBounds _screen;
    private readonly string _preferredMonitorId;
    private readonly AppSettings _settings;
    private IntPtr _hwnd;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private bool _isInitializing;
    private bool _showRequested;
    private IReadOnlyList<MonitorSelectionOption> _monitorOptions = [];
    private IReadOnlyList<DesktopWidgetSelectionOption> _desktopWidgetOptions = [];

    internal WidgetWindow(ScreenBounds screen, string preferredMonitorId)
    {
        _screen = screen;
        _preferredMonitorId = preferredMonitorId;
        _settings = AppSettings.Current;

        InitializeComponent();
        Title = "Veil Widget";

        Activated += OnFirstActivated;
        Closed += OnClosed;
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        try
        {
            AppLogger.Info("WidgetWindow first activation started.");
            _hwnd = WindowHelper.GetHwnd(this);
            ConfigureWindowChrome();
            SetupAcrylic();
            ApplyWindowSize();
            LoadSettings();
            AppLogger.Info("WidgetWindow first activation completed.");

            if (_showRequested)
            {
                DispatcherQueue.TryEnqueue(ShowCenteredCore);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("WidgetWindow failed during first activation.", ex);
            throw;
        }
    }

    internal void ShowCentered()
    {
        try
        {
            AppLogger.Info("WidgetWindow.ShowCentered called.");
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
            AppLogger.Error("WidgetWindow.ShowCentered failed.", ex);
            throw;
        }
    }

    private void ShowCenteredCore()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        _showRequested = false;
        ApplyWindowSize();
        Activate();
        SetForegroundWindow(_hwnd);
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
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        Title = string.Empty;

        string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Logo", "veil.ico");
        if (File.Exists(icoPath))
        {
            WindowHelper.SetWindowIcon(this, icoPath);
        }

        var appWindow = WindowHelper.GetAppWindow(this);
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
        }

        int style = GetWindowLongW(_hwnd, GWL_STYLE);
        style &= ~WS_MAXIMIZEBOX;
        style &= ~WS_THICKFRAME;
        SetWindowLongW(_hwnd, GWL_STYLE, style);
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED);

        var titleBar = appWindow.TitleBar;
        titleBar.BackgroundColor = global::Windows.UI.Color.FromArgb(255, 24, 24, 24);
        titleBar.ForegroundColor = global::Windows.UI.Color.FromArgb(0, 255, 255, 255);
        titleBar.InactiveBackgroundColor = global::Windows.UI.Color.FromArgb(255, 24, 24, 24);
        titleBar.InactiveForegroundColor = global::Windows.UI.Color.FromArgb(0, 255, 255, 255);
        titleBar.ButtonBackgroundColor = global::Windows.UI.Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonInactiveBackgroundColor = global::Windows.UI.Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonHoverBackgroundColor = global::Windows.UI.Color.FromArgb(48, 255, 255, 255);
        titleBar.ButtonPressedBackgroundColor = global::Windows.UI.Color.FromArgb(28, 255, 255, 255);
        titleBar.ButtonForegroundColor = global::Windows.UI.Color.FromArgb(230, 255, 255, 255);
        titleBar.ButtonHoverForegroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255);
        titleBar.ButtonPressedForegroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255);
        titleBar.ButtonInactiveForegroundColor = global::Windows.UI.Color.FromArgb(160, 255, 255, 255);
    }

    private void LoadSettings()
    {
        _isInitializing = true;
        LoadMonitorOptions();
        WidgetSnapToGridButton.IsChecked = _settings.DesktopWidgetSnapToGrid;
        WidgetGridSizeTextBox.Text = _settings.DesktopWidgetGridSize.ToString();
        LoadDesktopWidgetOptions();
        _isInitializing = false;
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

    private void OnWidgetSnapToGridChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _settings.DesktopWidgetSnapToGrid = WidgetSnapToGridButton.IsChecked == true;
    }

    private void OnWidgetGridSizeChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (int.TryParse(WidgetGridSizeTextBox.Text, out int grid))
        {
            _settings.DesktopWidgetGridSize = grid;
        }
    }

    private void OnAddDesktopWidgetClick(object sender, RoutedEventArgs e)
    {
        if (WidgetKindComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not string kind)
        {
            kind = "Clock";
        }

        string monitorId = _monitorOptions.FirstOrDefault(option =>
                string.Equals(option.Id, _preferredMonitorId, StringComparison.OrdinalIgnoreCase))?.Id
            ?? _monitorOptions.FirstOrDefault(static option => option.IsPrimary)?.Id
            ?? _monitorOptions.FirstOrDefault()?.Id
            ?? string.Empty;
        DesktopWidgetSetting created = _settings.AddDesktopWidget(kind, monitorId);
        LoadDesktopWidgetOptions(created.Id);
    }

    private void OnRemoveDesktopWidgetClick(object sender, RoutedEventArgs e)
    {
        if (DesktopWidgetComboBox.SelectedItem is not DesktopWidgetSelectionOption selection)
        {
            return;
        }

        _settings.RemoveDesktopWidget(selection.Id);
        LoadDesktopWidgetOptions();
    }

    private void OnDesktopWidgetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncSelectedWidgetEditors();
    }

    private void OnApplyDesktopWidgetClick(object sender, RoutedEventArgs e)
    {
        if (DesktopWidgetComboBox.SelectedItem is not DesktopWidgetSelectionOption selection)
        {
            return;
        }

        DesktopWidgetSetting? current = _settings.DesktopWidgets.FirstOrDefault(widget =>
            string.Equals(widget.Id, selection.Id, StringComparison.OrdinalIgnoreCase));
        if (current is null)
        {
            return;
        }

        int.TryParse(WidgetXTextBox.Text, out int x);
        int.TryParse(WidgetYTextBox.Text, out int y);
        int.TryParse(WidgetWidthTextBox.Text, out int width);
        int.TryParse(WidgetHeightTextBox.Text, out int height);
        double.TryParse(WidgetOpacityTextBox.Text, out double opacity);
        int.TryParse(WidgetCornerRadiusTextBox.Text, out int cornerRadius);
        double.TryParse(WidgetScaleTextBox.Text, out double scale);
        int.TryParse(WidgetRefreshTextBox.Text, out int refreshSeconds);

        string kind = WidgetKindComboBox.SelectedItem is ComboBoxItem { Tag: string kindTag }
            ? kindTag
            : current.Kind;

        DesktopWidgetSetting updated = current with
        {
            Kind = kind,
            Title = WidgetTitleTextBox.Text,
            MonitorId = WidgetMonitorIdTextBox.Text,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Opacity = opacity,
            CornerRadius = cornerRadius,
            Scale = scale,
            Theme = WidgetThemeComboBox.SelectedItem is ComboBoxItem { Tag: string theme } ? theme : current.Theme,
            BackgroundColor = WidgetBackgroundColorTextBox.Text,
            ForegroundColor = WidgetForegroundColorTextBox.Text,
            AccentColor = WidgetAccentColorTextBox.Text,
            RefreshSeconds = refreshSeconds,
            ShowSeconds = WidgetShowSecondsButton.IsChecked == true
        };

        _settings.UpdateDesktopWidget(updated);
        LoadDesktopWidgetOptions(updated.Id);
    }

    private void LoadDesktopWidgetOptions(string? selectedId = null)
    {
        _desktopWidgetOptions = _settings.DesktopWidgets
            .Select(widget => new DesktopWidgetSelectionOption(widget.Id, $"{widget.Title} ({widget.Kind})"))
            .ToArray();

        DesktopWidgetComboBox.ItemsSource = _desktopWidgetOptions;
        DesktopWidgetComboBox.SelectedItem = _desktopWidgetOptions.FirstOrDefault(option =>
            string.Equals(option.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? _desktopWidgetOptions.FirstOrDefault();
        SyncSelectedWidgetEditors();
    }

    private void SyncSelectedWidgetEditors()
    {
        bool hasSelection = DesktopWidgetComboBox.SelectedItem is DesktopWidgetSelectionOption selection;
        WidgetEditorPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        RemoveDesktopWidgetButton.IsEnabled = hasSelection;

        if (!hasSelection || DesktopWidgetComboBox.SelectedItem is not DesktopWidgetSelectionOption selected)
        {
            return;
        }

        DesktopWidgetSetting? widget = _settings.DesktopWidgets.FirstOrDefault(entry =>
            string.Equals(entry.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
        if (widget is null)
        {
            return;
        }

        _isInitializing = true;
        WidgetTitleTextBox.Text = widget.Title;
        WidgetXTextBox.Text = widget.X.ToString();
        WidgetYTextBox.Text = widget.Y.ToString();
        WidgetWidthTextBox.Text = widget.Width.ToString();
        WidgetHeightTextBox.Text = widget.Height.ToString();
        WidgetOpacityTextBox.Text = widget.Opacity.ToString("0.00");
        WidgetCornerRadiusTextBox.Text = widget.CornerRadius.ToString();
        WidgetScaleTextBox.Text = widget.Scale.ToString("0.00");
        WidgetRefreshTextBox.Text = widget.RefreshSeconds.ToString();
        WidgetMonitorIdTextBox.Text = widget.MonitorId;
        WidgetBackgroundColorTextBox.Text = widget.BackgroundColor;
        WidgetForegroundColorTextBox.Text = widget.ForegroundColor;
        WidgetAccentColorTextBox.Text = widget.AccentColor;
        WidgetShowSecondsButton.IsChecked = widget.ShowSeconds;

        foreach (ComboBoxItem item in WidgetKindComboBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string value && string.Equals(value, widget.Kind, StringComparison.Ordinal))
            {
                WidgetKindComboBox.SelectedItem = item;
                break;
            }
        }

        foreach (ComboBoxItem item in WidgetThemeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string value && string.Equals(value, widget.Theme, StringComparison.Ordinal))
            {
                WidgetThemeComboBox.SelectedItem = item;
                break;
            }
        }

        _isInitializing = false;
    }

    private void OnDoneClick(object sender, RoutedEventArgs e)
    {
        Close();
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

        int width = Math.Max(540, (int)Math.Round(screenWidth * 0.46));
        int height = Math.Max(520, (int)Math.Round(screenHeight * 0.72));

        PanelBorder.Width = double.NaN;
        PanelBorder.Height = double.NaN;

        WindowHelper.PositionOnMonitor(
            this,
            _screen.Left + ((screenWidth - width) / 2),
            _screen.Top + ((screenHeight - height) / 2),
            width,
            height);
    }
}
