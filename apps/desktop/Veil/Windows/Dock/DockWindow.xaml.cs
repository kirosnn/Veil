using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Veil.Diagnostics;
using Veil.Interop;
using Veil.Services;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class DockWindow : Window
{
    private const int ReservedHeight = 108;
    private const int DockItemSize = 66;
    private const int DockIconSize = 54;
    private const int DockIconCornerRadius = 18;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(900);

    private readonly string _monitorId;
    private readonly ScreenBounds _screen;
    private readonly DispatcherTimer _refreshTimer;
    private IntPtr _hwnd;
    private bool _appBarRegistered;
    private bool _isHidden;
    private string _lastStateSignature = string.Empty;

    internal DockWindow(string monitorId, ScreenBounds screen)
    {
        _monitorId = monitorId;
        _screen = screen;

        InitializeComponent();
        Title = "Veil Dock";
        Activated += OnActivated;
        Closed += OnClosed;

        _refreshTimer = new DispatcherTimer
        {
            Interval = RefreshInterval
        };
        _refreshTimer.Tick += OnRefreshTick;
    }

    internal ScreenBounds ScreenBounds => _screen;

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnActivated;

        _hwnd = WindowHelper.GetHwnd(this);
        WindowHelper.RemoveTitleBar(this);
        WindowHelper.ExtendFrameIntoClientArea(this);
        WindowHelper.MakeOverlay(this);
        PositionDock();
        WindowHelper.SetAlwaysOnTop(this);
        WindowHelper.RegisterAppBar(this, _screen, ABE_BOTTOM, ReservedHeight);
        _appBarRegistered = true;
        _refreshTimer.Start();
        RefreshDock(force: true);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTick;

        if (_appBarRegistered)
        {
            WindowHelper.UnregisterAppBar(this);
            _appBarRegistered = false;
        }

        AppLogger.Info($"DockWindow closed for {_monitorId}. Hidden={_isHidden}.");
    }

    private void OnRefreshTick(object? sender, object e)
    {
        RefreshDock(force: false);
    }

    private void RefreshDock(bool force)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        bool shouldHide = WindowHelper.IsForegroundMaximizedOrFullscreen(_screen, _hwnd);
        if (shouldHide != _isHidden)
        {
            _isHidden = shouldHide;
            if (_isHidden)
            {
                if (_appBarRegistered)
                {
                    WindowHelper.UnregisterAppBar(this);
                    _appBarRegistered = false;
                }

                WindowHelper.HideWindow(this);
            }
            else
            {
                PositionDock();
                WindowHelper.ShowWindow(this);
                WindowHelper.SetAlwaysOnTop(this);
                WindowHelper.RegisterAppBar(this, _screen, ABE_BOTTOM, ReservedHeight);
                _appBarRegistered = true;
            }
        }

        if (_isHidden)
        {
            return;
        }

        IReadOnlyList<WindowSwitchEntry> windows = WindowSwitcherService.GetSwitchableWindows();
        string stateSignature = string.Join(
            "|",
            windows.Select(static entry => $"{entry.Handle}:{entry.AppName}:{entry.WindowTitle}:{entry.IsMinimized}"));

        if (!force && string.Equals(stateSignature, _lastStateSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastStateSignature = stateSignature;
        RebuildItems(windows);
    }

    private void PositionDock()
    {
        int width = Math.Max(1, _screen.Right - _screen.Left);
        int x = _screen.Left;
        int y = _screen.Bottom - ReservedHeight;
        WindowHelper.PositionOnMonitor(this, x, y, width, ReservedHeight);
    }

    private void RebuildItems(IReadOnlyList<WindowSwitchEntry> windows)
    {
        ItemsPanel.Children.Clear();

        if (windows.Count == 0)
        {
            StatusText.Text = "No windows available";
            return;
        }

        StatusText.Text = windows.Count == 1 ? "1 open window" : $"{windows.Count} open windows";

        foreach (WindowSwitchEntry entry in windows.Take(16))
        {
            Button button = CreateDockButton(entry);
            ItemsPanel.Children.Add(button);
        }
    }

    private Button CreateDockButton(WindowSwitchEntry entry)
    {
        var scaleTransform = new ScaleTransform
        {
            ScaleX = 1,
            ScaleY = 1
        };

        var button = new Button
        {
            Width = DockItemSize,
            Height = DockItemSize,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = scaleTransform,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 1.0)
        };

        button.PointerEntered += OnDockButtonPointerEntered;
        button.PointerExited += OnDockButtonPointerExited;
        button.Click += (_, _) => WindowSwitcherService.ActivateWindow(entry.Handle);

        ToolTipService.SetToolTip(button, entry.DisplayTitle);

        global::Windows.UI.Color tileColor = entry.AccentColor;
        global::Windows.UI.Color labelColor = UseDarkForeground(tileColor)
            ? global::Windows.UI.Color.FromArgb(255, 26, 28, 32)
            : global::Windows.UI.Color.FromArgb(255, 248, 250, 252);

        var layout = new Grid();

        var tile = new Border
        {
            Width = DockIconSize,
            Height = DockIconSize,
            CornerRadius = new CornerRadius(DockIconCornerRadius),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(entry.IsMinimized ? (byte)152 : (byte)234, tileColor.R, tileColor.G, tileColor.B)),
            BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(48, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top
        };

        tile.Child = new TextBlock
        {
            Text = GetMonogram(entry.AppName),
            FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
            FontSize = 19,
            Foreground = new SolidColorBrush(labelColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        layout.Children.Add(tile);

        var indicator = new Border
        {
            Width = entry.IsMinimized ? 8 : 18,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(entry.IsMinimized ? (byte)108 : (byte)228, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2)
        };

        layout.Children.Add(indicator);
        button.Content = layout;
        return button;
    }

    private static bool UseDarkForeground(global::Windows.UI.Color color)
    {
        double luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
        return luminance >= 0.62;
    }

    private static string GetMonogram(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "A";
        }

        string[] parts = text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length >= 2)
        {
            return string.Concat(parts[0][0], parts[1][0]).ToUpperInvariant();
        }

        string value = parts[0];
        return value.Length >= 2
            ? value[..2].ToUpperInvariant()
            : value[..1].ToUpperInvariant();
    }

    private static void OnDockButtonPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button && button.RenderTransform is ScaleTransform transform)
        {
            transform.ScaleX = 1.14;
            transform.ScaleY = 1.14;
        }
    }

    private static void OnDockButtonPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button && button.RenderTransform is ScaleTransform transform)
        {
            transform.ScaleX = 1;
            transform.ScaleY = 1;
        }
    }
}
