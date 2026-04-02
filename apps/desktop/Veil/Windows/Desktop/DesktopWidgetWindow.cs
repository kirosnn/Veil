using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Veil.Configuration;
using Veil.Interop;
using Veil.Services;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

internal sealed class DesktopWidgetWindow : Window
{
    private readonly WeatherService _weatherService;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _geometryTimer;
    private readonly Border _card;
    private readonly Border _specularLayer;
    private readonly Border _depthLayer;
    private readonly Border _innerGlowLayer;
    private readonly StackPanel _stack;
    private readonly Border _accentBar;
    private readonly Grid _footerGrid;
    private readonly TextBlock _badgeText;
    private readonly TextBlock _titleText;
    private readonly TextBlock _valueText;
    private readonly TextBlock _metaText;
    private readonly AppSettings _settings;
    private IntPtr _hwnd;
    private DesktopWidgetSetting _widget;
    private MonitorInfo2? _monitor;
    private DesktopWidgetSetting? _lastAppliedWidget;
    private string _lastAppliedMonitorId = string.Empty;
    private Rect _lastAppliedMonitorWorkArea;
    private bool _lastAppliedSnapToGrid;
    private int _lastAppliedGridSize;
    private Rect _lastRect;
    private DateTime _lastRectChangeUtc;
    private bool _pendingPersist;
    private bool _ignoreGeometry;

    internal event Action<string, int, int, string>? WidgetMoved;

    internal DesktopWidgetWindow(string widgetId, WeatherService weatherService)
    {
        _weatherService = weatherService;
        _settings = AppSettings.Current;
        _widget = new DesktopWidgetSetting(widgetId, "Clock", "Clock", string.Empty, 56, 56, 260, 118, 0.90, 22, 1.0, "Dark", "#101114", "#F4F4F5", "#7EC7FF", 1, true);

        _refreshTimer = new DispatcherTimer();
        _refreshTimer.Tick += OnRefreshTick;

        _geometryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _geometryTimer.Tick += OnGeometryTick;

        var rootGrid = new Grid { Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)) };
        _card = new Border();
        _depthLayer = new Border { IsHitTestVisible = false };
        _specularLayer = new Border { IsHitTestVisible = false };
        _innerGlowLayer = new Border { IsHitTestVisible = false };
        _stack = new StackPanel { Spacing = 8 };
        _accentBar = new Border { Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 2, 10, 0) };
        _footerGrid = new Grid();
        _badgeText = new TextBlock { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right };
        _titleText = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        _valueText = new TextBlock { FontSize = 32, FontFamily = (FontFamily)Application.Current.Resources["SfDisplaySemibold"], TextWrapping = TextWrapping.NoWrap };
        _metaText = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 3 };
        _footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _stack.Children.Add(_titleText);
        _stack.Children.Add(_valueText);
        _stack.Children.Add(_metaText);
        _footerGrid.Children.Add(_accentBar);
        Grid.SetColumn(_badgeText, 1);
        _footerGrid.Children.Add(_badgeText);
        _stack.Children.Add(_footerGrid);
        _card.Child = _stack;
        _card.PointerPressed += OnCardPointerPressed;
        rootGrid.Children.Add(_depthLayer);
        rootGrid.Children.Add(_card);
        rootGrid.Children.Add(_innerGlowLayer);
        rootGrid.Children.Add(_specularLayer);
        Content = rootGrid;

        Activated += OnFirstActivated;
        Closed += OnClosed;
    }

    internal void Initialize()
    {
        WindowHelper.GetAppWindow(this).MoveAndResize(new global::Windows.Graphics.RectInt32(-9999, -9999, 1, 1));
        Activate();
    }

    internal void ApplyWidget(DesktopWidgetSetting widget, MonitorInfo2 monitor)
    {
        _widget = widget;
        _monitor = monitor;

        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        ApplyCurrentWidget();
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        _hwnd = WindowHelper.GetHwnd(this);
        WindowHelper.RemoveTitleBar(this);

        int exStyle = GetWindowLongW(_hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        exStyle |= WS_EX_NOACTIVATE;
        exStyle &= ~WS_EX_TOPMOST;
        SetWindowLongW(_hwnd, GWL_EXSTYLE, exStyle);

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        WindowHelper.AttachToDesktop(this);
        SetupAcrylic();
        ShowWindowNative(_hwnd, SW_SHOWNOACTIVATE);

        _refreshTimer.Start();
        _geometryTimer.Start();
        ApplyCurrentWidget();
    }

    private void SetupAcrylic()
    {
    }

    private void ApplyBounds(MonitorInfo2 monitor, bool snapToGrid)
    {
        int width = _widget.Width;
        int height = _widget.Height;
        int x = monitor.WorkArea.Left + _widget.X;
        int y = monitor.WorkArea.Top + _widget.Y;

        if (snapToGrid)
        {
            int grid = _settings.DesktopWidgetGridSize;
            x = monitor.WorkArea.Left + Snap(_widget.X, grid);
            y = monitor.WorkArea.Top + Snap(_widget.Y, grid);
        }

        x = Math.Clamp(x, monitor.WorkArea.Left, monitor.WorkArea.Right - width);
        y = Math.Clamp(y, monitor.WorkArea.Top, monitor.WorkArea.Bottom - height);

        _ignoreGeometry = true;
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, width, height, SWP_NOACTIVATE | SWP_NOZORDER);
        WindowHelper.ApplyRoundedRegion(_hwnd, width, height, _widget.CornerRadius);
        _ignoreGeometry = false;
    }

    private void ApplyCurrentWidget()
    {
        if (_hwnd == IntPtr.Zero || _monitor is null)
        {
            return;
        }

        if (_lastAppliedWidget == _widget
            && string.Equals(_lastAppliedMonitorId, _monitor.Id, StringComparison.OrdinalIgnoreCase)
            && _lastAppliedMonitorWorkArea.Equals(_monitor.WorkArea)
            && _lastAppliedSnapToGrid == _settings.DesktopWidgetSnapToGrid
            && _lastAppliedGridSize == _settings.DesktopWidgetGridSize)
        {
            return;
        }

        ApplyVisualStyle();
        ApplyBounds(_monitor, snapToGrid: _settings.DesktopWidgetSnapToGrid);
        RefreshData();
        _lastAppliedWidget = _widget;
        _lastAppliedMonitorId = _monitor.Id;
        _lastAppliedMonitorWorkArea = _monitor.WorkArea;
        _lastAppliedSnapToGrid = _settings.DesktopWidgetSnapToGrid;
        _lastAppliedGridSize = _settings.DesktopWidgetGridSize;
    }

    private void ApplyVisualStyle()
    {
        SetupAcrylic();

        global::Windows.UI.Color background = ParseColor(_widget.BackgroundColor, (byte)Math.Round(_widget.Opacity * 255));
        _card.CornerRadius = new CornerRadius(_widget.CornerRadius);
        _card.Padding = new Thickness(18 * _widget.Scale, 15 * _widget.Scale, 18 * _widget.Scale, 15 * _widget.Scale);
        _card.Background = new SolidColorBrush(background);
        _card.BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(
            _widget.Theme == "Light" ? (byte)46 : (byte)62,
            255,
            255,
            255));
        _card.BorderThickness = new Thickness(1);

        global::Windows.UI.Color foreground = ParseColor(_widget.ForegroundColor);
        global::Windows.UI.Color accent = ParseColor(_widget.AccentColor);
        bool lightTheme = _widget.Theme == "Light";

        _depthLayer.CornerRadius = new CornerRadius(_widget.CornerRadius + 4);
        _depthLayer.Margin = new Thickness(0, 2, 0, 0);
        _depthLayer.Background = new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, 0),
            EndPoint = new global::Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = global::Windows.UI.Color.FromArgb(lightTheme ? (byte)28 : (byte)46, 255, 255, 255), Offset = 0.0 },
                new GradientStop { Color = global::Windows.UI.Color.FromArgb(lightTheme ? (byte)8 : (byte)24, 0, 0, 0), Offset = 1.0 }
            }
        };

        _specularLayer.CornerRadius = new CornerRadius(_widget.CornerRadius);
        _specularLayer.BorderThickness = new Thickness(0);
        _specularLayer.Background = new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, 0),
            EndPoint = new global::Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = global::Windows.UI.Color.FromArgb(lightTheme ? (byte)62 : (byte)76, 255, 255, 255), Offset = 0.0 },
                new GradientStop { Color = global::Windows.UI.Color.FromArgb(lightTheme ? (byte)14 : (byte)18, 255, 255, 255), Offset = 0.24 },
                new GradientStop { Color = global::Windows.UI.Color.FromArgb(0, 255, 255, 255), Offset = 0.62 },
                new GradientStop { Color = global::Windows.UI.Color.FromArgb(lightTheme ? (byte)10 : (byte)22, 0, 0, 0), Offset = 1.0 }
            }
        };

        _innerGlowLayer.CornerRadius = new CornerRadius(_widget.CornerRadius);
        _innerGlowLayer.Margin = new Thickness(1);
        _innerGlowLayer.Background = new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, 0),
            EndPoint = new global::Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = global::Windows.UI.Color.FromArgb(lightTheme ? (byte)24 : (byte)38, 255, 255, 255), Offset = 0.0 },
                new GradientStop { Color = global::Windows.UI.Color.FromArgb(0, 255, 255, 255), Offset = 0.55 },
                new GradientStop { Color = global::Windows.UI.Color.FromArgb(lightTheme ? (byte)12 : (byte)24, 0, 0, 0), Offset = 1.0 }
            }
        };

        _titleText.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(214, foreground.R, foreground.G, foreground.B));
        _valueText.Foreground = new SolidColorBrush(accent);
        _metaText.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(180, foreground.R, foreground.G, foreground.B));
        _badgeText.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(lightTheme ? (byte)140 : (byte)170, foreground.R, foreground.G, foreground.B));
        _accentBar.CornerRadius = new CornerRadius(2);
        _accentBar.Background = new LinearGradientBrush
        {
            StartPoint = new global::Windows.Foundation.Point(0, 0),
            EndPoint = new global::Windows.Foundation.Point(1, 0),
            GradientStops =
            {
                new GradientStop { Color = global::Windows.UI.Color.FromArgb(240, accent.R, accent.G, accent.B), Offset = 0.0 },
                new GradientStop { Color = global::Windows.UI.Color.FromArgb(140, accent.R, accent.G, accent.B), Offset = 1.0 }
            }
        };

        _titleText.FontSize = 12 * _widget.Scale;
        _valueText.FontSize = 32 * _widget.Scale;
        _metaText.FontSize = 11 * _widget.Scale;
        _badgeText.FontSize = 10 * _widget.Scale;

        _refreshTimer.Interval = TimeSpan.FromSeconds(_widget.RefreshSeconds);
    }

    private void OnCardPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        ReleaseCapture();
        SendMessageW(_hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    private void OnGeometryTick(object? sender, object e)
    {
        if (_hwnd == IntPtr.Zero || _ignoreGeometry || !GetWindowRect(_hwnd, out Rect rect))
        {
            return;
        }

        if (rect.Left != _lastRect.Left || rect.Top != _lastRect.Top)
        {
            _lastRect = rect;
            _lastRectChangeUtc = DateTime.UtcNow;
            _pendingPersist = true;
            return;
        }

        if (!_pendingPersist || (DateTime.UtcNow - _lastRectChangeUtc).TotalMilliseconds < 320)
        {
            return;
        }

        _pendingPersist = false;
        PersistMovedPosition(rect);
    }

    private void PersistMovedPosition(Rect rect)
    {
        MonitorInfo2 monitor = ResolveMonitor(rect);
        int relativeX = rect.Left - monitor.WorkArea.Left;
        int relativeY = rect.Top - monitor.WorkArea.Top;

        if (_settings.DesktopWidgetSnapToGrid)
        {
            int grid = _settings.DesktopWidgetGridSize;
            relativeX = Snap(relativeX, grid);
            relativeY = Snap(relativeY, grid);
            ApplyBounds(monitor, snapToGrid: true);
        }

        WidgetMoved?.Invoke(_widget.Id, relativeX, relativeY, monitor.Id);
    }

    private static int Snap(int value, int grid)
    {
        if (grid <= 1)
        {
            return value;
        }

        return (int)Math.Round(value / (double)grid) * grid;
    }

    private static MonitorInfo2 ResolveMonitor(Rect rect)
    {
        int centerX = rect.Left + ((rect.Right - rect.Left) / 2);
        int centerY = rect.Top + ((rect.Bottom - rect.Top) / 2);

        List<MonitorInfo2> monitors = MonitorService.GetAllMonitors();
        MonitorInfo2? monitor = monitors.FirstOrDefault(m =>
            centerX >= m.WorkArea.Left && centerX < m.WorkArea.Right &&
            centerY >= m.WorkArea.Top && centerY < m.WorkArea.Bottom);

        return monitor ?? monitors.First(static m => m.IsPrimary);
    }

    private void OnRefreshTick(object? sender, object e)
    {
        RefreshData();
    }

    private void RefreshData()
    {
        _titleText.Text = _widget.Title;

        if (_widget.Kind == "Clock")
        {
            string format = _widget.ShowSeconds ? "HH:mm:ss" : "HH:mm";
            _valueText.Text = DateTime.Now.ToString(format);
            _metaText.Text = DateTime.Now.ToString("dddd, d MMMM", System.Globalization.CultureInfo.InvariantCulture);
            _badgeText.Text = DateTime.Now.ToString("UTCzzz");
            return;
        }

        if (_widget.Kind == "Weather")
        {
            WeatherSnapshot? snapshot = _weatherService.Snapshot;
            if (snapshot is null)
            {
                _valueText.Text = "--";
                _metaText.Text = "Loading weather";
                _badgeText.Text = "SYNC";
                _ = _weatherService.RefreshAsync(true);
                return;
            }

            _valueText.Text = $"{Math.Round(snapshot.PrimaryCity.TemperatureC):0}°";
            _metaText.Text = $"{snapshot.PrimaryCity.DisplayName}  {WeatherService.GetConditionLabel(snapshot.PrimaryCity.WeatherCode)}  H {Math.Round(snapshot.PrimaryCity.HighTemperatureC):0}°  L {Math.Round(snapshot.PrimaryCity.LowTemperatureC):0}°";
            _badgeText.Text = $"{snapshot.PrimaryCity.HumidityPercent}%";
            return;
        }

        var memoryStatus = MemoryStatusEx.Create();
        if (!GlobalMemoryStatusEx(ref memoryStatus) || memoryStatus.ullTotalPhys == 0)
        {
            _valueText.Text = "--";
            _metaText.Text = "System unavailable";
            _badgeText.Text = "N/A";
            return;
        }

        double usedPercent = (memoryStatus.ullTotalPhys - memoryStatus.ullAvailPhys) / (double)memoryStatus.ullTotalPhys * 100;
        using Process process = Process.GetCurrentProcess();
        _valueText.Text = $"RAM {usedPercent:0}%";
        _metaText.Text = $"CPU threads: {Environment.ProcessorCount}  Veil WS: {process.WorkingSet64 / (1024 * 1024)} MB";
        _badgeText.Text = $"{(memoryStatus.ullAvailPhys / (1024.0 * 1024 * 1024)):0.0} GB free";
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _refreshTimer.Stop();
        _geometryTimer.Stop();
    }

    private static global::Windows.UI.Color ParseColor(string hex, byte? alpha = null)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return global::Windows.UI.Color.FromArgb(alpha ?? 255, 255, 255, 255);
        }

        string value = hex.Trim();
        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        if (value.Length != 7)
        {
            return global::Windows.UI.Color.FromArgb(alpha ?? 255, 255, 255, 255);
        }

        byte r = Convert.ToByte(value[1..3], 16);
        byte g = Convert.ToByte(value[3..5], 16);
        byte b = Convert.ToByte(value[5..7], 16);

        return global::Windows.UI.Color.FromArgb(alpha ?? 255, r, g, b);
    }
}
