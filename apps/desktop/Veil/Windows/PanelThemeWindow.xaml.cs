using Veil.Configuration;
using Veil.Interop;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class PanelThemeWindow : Window
{
    private const int PanelWidth = 248;
    private const int MinimumPanelWidth = 220;
    private const int MinimumPanelHeight = 96;
    private const int PanelCornerRadius = 14;
    private const int PanelWidthPadding = 16;
    private const int PanelHeightPadding = 22;
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly DispatcherTimer _visibilityTimer;
    private IntPtr _hwnd;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private DateTime _openedAtUtc;
    private int _panelWidth = PanelWidth;
    private int _panelHeight = 126;

    public bool IsPanelVisible { get; private set; }
    public DateTime LastHiddenAtUtc { get; private set; }

    public PanelThemeWindow()
    {
        InitializeComponent();
        Title = "Panel Theme";

        _visibilityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _visibilityTimer.Tick += OnVisibilityTick;

        Activated += OnFirstActivated;
        Activated += OnWindowActivated;
        Closed += OnClosed;
        _settings.Changed += OnSettingsChanged;
    }

    private bool UseLightTheme => _settings.TopBarPanelTheme == "Light";

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        _hwnd = WindowHelper.GetHwnd(this);
        WindowHelper.RemoveTitleBar(this);

        int exStyle = GetWindowLongW(_hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        exStyle &= ~WS_EX_NOACTIVATE;
        SetWindowLongW(_hwnd, GWL_EXSTYLE, exStyle);

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        SetupAcrylic();
        BuildUI(0, 0);
        ShowWindowNative(_hwnd, SW_HIDE);
    }

    private void SetupAcrylic()
    {
        _acrylicController?.Dispose();

        _acrylicController = new DesktopAcrylicController
        {
            TintColor = UseLightTheme
                ? global::Windows.UI.Color.FromArgb(255, 255, 255, 255)
                : global::Windows.UI.Color.FromArgb(255, 0, 0, 0),
            TintOpacity = UseLightTheme ? 0.06f : 0.68f,
            LuminosityOpacity = UseLightTheme ? 0.78f : 0.22f,
            FallbackColor = UseLightTheme
                ? global::Windows.UI.Color.FromArgb(216, 255, 255, 255)
                : global::Windows.UI.Color.FromArgb(232, 0, 0, 0)
        };

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = UseLightTheme ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark
        };

        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
    }

    private PanelWindowMetrics BuildUI(int anchorRight, int anchorY)
    {
        ContentPanel.Children.Clear();

        var shell = new Border
        {
            CornerRadius = new CornerRadius(PanelCornerRadius),
            Background = new SolidColorBrush(UseLightTheme
                ? global::Windows.UI.Color.FromArgb(42, 255, 255, 255)
                : global::Windows.UI.Color.FromArgb(42, 0, 0, 0)),
            Padding = new Thickness(14),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var stack = new StackPanel
        {
            Spacing = 12
        };

        stack.Children.Add(new TextBlock
        {
            Text = "Panel Theme",
            FontSize = 13,
            FontFamily = (FontFamily)Application.Current.Resources["SfDisplaySemibold"],
            Foreground = CreatePrimaryTextBrush()
        });

        stack.Children.Add(CreateThemeRow("Menus", _settings.TopBarPanelTheme, OnThemeClick));

        shell.Child = stack;
        PanelWindowMetrics metrics = PanelWindowSizer.Measure(
            shell,
            anchorRight,
            anchorY,
            PanelWidth,
            MinimumPanelWidth,
            MinimumPanelHeight,
            PanelWidthPadding,
            PanelHeightPadding);
        if (metrics.IsHeightClamped)
        {
            shell.Child = CreateScrollableContent(stack);
            shell.Width = metrics.Width;
            shell.MaxWidth = metrics.Width;
            shell.Height = metrics.Height;
        }
        _panelWidth = metrics.Width;
        _panelHeight = metrics.Height;
        ContentPanel.Children.Add(shell);
        return metrics;
    }

    private static ScrollViewer CreateScrollableContent(UIElement content)
    {
        return new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(0, 0, 6, 0),
                Child = content
            },
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private UIElement CreateThemeRow(string label, string selectedTheme, RoutedEventHandler clickHandler)
    {
        var stack = new StackPanel
        {
            Spacing = 6
        };

        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            Foreground = CreateSecondaryTextBrush()
        });

        var grid = new Grid
        {
            ColumnSpacing = 8
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lightButton = CreateThemeButton("Light", selectedTheme == "Light", clickHandler, label);
        Grid.SetColumn(lightButton, 0);
        grid.Children.Add(lightButton);

        var darkButton = CreateThemeButton("Dark", selectedTheme == "Dark", clickHandler, label);
        Grid.SetColumn(darkButton, 1);
        grid.Children.Add(darkButton);

        stack.Children.Add(grid);
        return stack;
    }

    private Button CreateThemeButton(string theme, bool isSelected, RoutedEventHandler clickHandler, string tagPrefix)
    {
        var button = PanelButtonFactory.Create(
            theme,
            new SolidColorBrush(UseLightTheme
                ? global::Windows.UI.Color.FromArgb(isSelected ? (byte)34 : (byte)16, 0, 0, 0)
                : global::Windows.UI.Color.FromArgb(isSelected ? (byte)52 : (byte)18, 255, 255, 255)),
            new SolidColorBrush(UseLightTheme
                ? global::Windows.UI.Color.FromArgb(isSelected ? (byte)255 : (byte)214, 0, 0, 0)
                : global::Windows.UI.Color.FromArgb(isSelected ? (byte)255 : (byte)214, 255, 255, 255)),
            new SolidColorBrush(UseLightTheme
                ? global::Windows.UI.Color.FromArgb(isSelected ? (byte)42 : (byte)24, 0, 0, 0)
                : global::Windows.UI.Color.FromArgb(isSelected ? (byte)64 : (byte)28, 255, 255, 255)),
            new SolidColorBrush(UseLightTheme
                ? global::Windows.UI.Color.FromArgb(isSelected ? (byte)28 : (byte)14, 0, 0, 0)
                : global::Windows.UI.Color.FromArgb(isSelected ? (byte)42 : (byte)14, 255, 255, 255)),
            clickHandler,
            height: 34,
            cornerRadius: new CornerRadius(10),
            tag: $"{tagPrefix}:{theme}");
        button.FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"];
        return button;
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: "Menus:Light" })
        {
            _settings.TopBarPanelTheme = "Light";
            return;
        }

        _settings.TopBarPanelTheme = "Dark";
    }

    private void OnSettingsChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            SetupAcrylic();
            if (!GetWindowRect(_hwnd, out var rect))
            {
                BuildUI(0, 0);
                return;
            }

            PanelWindowMetrics metrics = BuildUI(rect.Right, rect.Top);
            if (!IsPanelVisible)
            {
                return;
            }

            var appWindow = WindowHelper.GetAppWindow(this);
            appWindow.MoveAndResize(metrics.Bounds);
            WindowHelper.ApplyRoundedRegion(_hwnd, metrics.Bounds.Width, metrics.Bounds.Height, PanelCornerRadius);
        });
    }

    public void Initialize()
    {
        var appWindow = WindowHelper.GetAppWindow(this);
        appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(-9999, -9999, 1, 1));
        Activate();
    }

    public void ShowAt(int x, int y)
    {
        PanelWindowMetrics metrics = BuildUI(x, y + 6);

        var appWindow = WindowHelper.GetAppWindow(this);
        appWindow.MoveAndResize(metrics.Bounds);
        WindowHelper.ApplyRoundedRegion(_hwnd, metrics.Bounds.Width, metrics.Bounds.Height, PanelCornerRadius);

        appWindow.Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        Activate();
        SetForegroundWindow(_hwnd);

        IsPanelVisible = true;
        _openedAtUtc = DateTime.UtcNow;
        _visibilityTimer.Start();
    }

    public void Hide()
    {
        _visibilityTimer.Stop();
        ShowWindowNative(_hwnd, SW_HIDE);
        IsPanelVisible = false;
        LastHiddenAtUtc = DateTime.UtcNow;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!IsPanelVisible) return;
        if (args.WindowActivationState != WindowActivationState.Deactivated) return;
        if ((DateTime.UtcNow - _openedAtUtc).TotalMilliseconds < 180) return;
        Hide();
    }

    private void OnVisibilityTick(object? sender, object e)
    {
        if (!IsPanelVisible) return;
        if ((DateTime.UtcNow - _openedAtUtc).TotalMilliseconds < 220) return;
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _hwnd) return;
        Hide();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _settings.Changed -= OnSettingsChanged;
    }

    private SolidColorBrush CreatePrimaryTextBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(230, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(234, 255, 255, 255));
    }

    private SolidColorBrush CreateSecondaryTextBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(180, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(180, 255, 255, 255));
    }

}
