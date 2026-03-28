using Veil.Interop;
using Veil.Configuration;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using static Veil.Interop.NativeMethods;
using WinRT;

namespace Veil.Windows;

public sealed partial class MenuWindow : Window
{
    private const int MenuWidth = 150;
    private const int MenuCornerRadius = 14;
    private const int MenuItemHeight = 26;
    private const int MenuOuterPadding = 12;
    private const int MenuItemSpacing = 2;

    private readonly IntPtr _ownerHwnd;
    private readonly AppSettings _settings;
    private IntPtr _hwnd;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private readonly DispatcherTimer _visibilityTimer;
    private DateTime _openedAtUtc;
    private bool _useDarkForeground;

    public DateTime LastHiddenAtUtc { get; private set; }
    public bool IsMenuVisible { get; private set; }

    public event Action<string>? MenuItemClicked;

    private sealed record MenuEntry(string Id, string Label);

    private static readonly MenuEntry[] MenuItems =
    [
        new("Settings", "Settings")
    ];

    public MenuWindow(IntPtr ownerHwnd)
    {
        _ownerHwnd = ownerHwnd;
        _settings = AppSettings.Current;

        InitializeComponent();
        Title = "Veil Menu";
        BuildMenuItems();

        _visibilityTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _visibilityTimer.Tick += OnVisibilityTick;

        Activated += OnFirstActivated;
        Activated += OnWindowActivated;
        _settings.Changed += OnSettingsChanged;
    }

    private void SetupAcrylic()
    {
        _acrylicController?.Dispose();

        _acrylicController = new DesktopAcrylicController
        {
            TintColor = global::Windows.UI.Color.FromArgb(238, 26, 26, 30),
            TintOpacity = (float)_settings.MenuTintOpacity,
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
        ApplySettings();
        ShowWindowNative(_hwnd, SW_HIDE);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!IsMenuVisible)
        {
            return;
        }

        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            return;
        }

        if ((DateTime.UtcNow - _openedAtUtc).TotalMilliseconds < 180)
        {
            return;
        }

        Hide();
    }

    private void OnVisibilityTick(object? sender, object e)
    {
        if (!IsMenuVisible)
        {
            return;
        }

        if ((DateTime.UtcNow - _openedAtUtc).TotalMilliseconds < 220)
        {
            return;
        }

        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return;
        }

        if (foregroundWindow == _hwnd || foregroundWindow == _ownerHwnd || IsSameProcessWindow(foregroundWindow))
        {
            return;
        }

        Hide();
    }

    public void Initialize()
    {
        var appWindow = WindowHelper.GetAppWindow(this);
        appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(-9999, -9999, 1, 1));
        Activate();
    }

    public void ShowAt(int x, int y)
    {
        int height = CalculateMenuHeight();
        _useDarkForeground = ReadableSurfaceHelper.ShouldUseDarkForeground(x + 6, y + 6, MenuWidth, height);
        ApplySettings();
        BuildMenuItems();

        var appWindow = WindowHelper.GetAppWindow(this);
        appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(x + 6, y + 6, MenuWidth, height));
        WindowHelper.ApplyRoundedRegion(_hwnd, MenuWidth, height, MenuCornerRadius);

        appWindow.Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        Activate();
        SetForegroundWindow(_hwnd);

        IsMenuVisible = true;
        _openedAtUtc = DateTime.UtcNow;
        _visibilityTimer.Start();
    }

    public void Hide()
    {
        _visibilityTimer.Stop();
        ShowWindowNative(_hwnd, SW_HIDE);
        IsMenuVisible = false;
        LastHiddenAtUtc = DateTime.UtcNow;
    }

    private void OnSettingsChanged()
    {
        DispatcherQueue.TryEnqueue(ApplySettings);
    }

    private void ApplySettings()
    {
        if (_acrylicController != null)
        {
            _acrylicController.TintOpacity = (float)_settings.MenuTintOpacity;
            _acrylicController.FallbackColor = global::Windows.UI.Color.FromArgb(
                (byte)Math.Round(120 + (_settings.MenuTintOpacity * 200)),
                20, 20, 24);
        }

        PanelBorder.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(
            (byte)Math.Round(_settings.MenuTintOpacity * 255), 255, 255, 255));
    }

    private int CalculateMenuHeight()
    {
        if (MenuItems.Length == 0)
        {
            return MenuOuterPadding;
        }

        return MenuOuterPadding + (MenuItems.Length * MenuItemHeight) + ((MenuItems.Length - 1) * MenuItemSpacing);
    }

    private void BuildMenuItems()
    {
        MenuItemsPanel.Children.Clear();

        foreach (var entry in MenuItems)
        {
            var labelBrush = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground, 235);

            var label = new TextBlock
            {
                Text = entry.Label,
                FontSize = 12,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
                Foreground = labelBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var content = new Grid
            {
                Children =
                {
                    label
                }
            };

            var button = PanelButtonFactory.Create(
                content,
                new SolidColorBrush(Colors.Transparent),
                labelBrush,
                new SolidColorBrush(ColorHelper.FromArgb(28, 255, 255, 255)),
                new SolidColorBrush(ColorHelper.FromArgb(18, 255, 255, 255)),
                OnItemClick,
                height: MenuItemHeight,
                padding: new Thickness(10, 0, 10, 0),
                cornerRadius: new CornerRadius(8),
                tag: entry.Id,
                horizontalAlignment: HorizontalAlignment.Stretch,
                horizontalContentAlignment: HorizontalAlignment.Stretch);

            button.PointerEntered += (_, _) =>
            {
                label.Foreground = ReadableSurfaceHelper.CreateTextBrush(_useDarkForeground);
            };

            button.PointerExited += (_, _) =>
            {
                label.Foreground = labelBrush;
            };

            button.PointerCanceled += (_, _) =>
            {
                label.Foreground = labelBrush;
            };

            MenuItemsPanel.Children.Add(button);
        }
    }

    private void OnItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string label })
        {
            Hide();
            MenuItemClicked?.Invoke(label);
        }
    }

    private static bool IsSameProcessWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint processId);
        return processId == Environment.ProcessId;
    }
}
