using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Veil.Interop;
using Veil.Services;
using WinRT;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class AltTabWindow : Window
{
    private const int DefaultPanelWidth = 1240;
    private const int DefaultPanelHeight = 300;
    private const int MinimumPanelWidth = 860;
    private const int MinimumPanelHeight = 240;
    private const int PanelCornerRadius = 30;
    private const int CardWidth = 188;
    private const int CardHeight = 134;
    private const int CardRadius = 18;
    private const int CardSpacing = 14;

    private IntPtr _hwnd;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private bool _isVisible;

    public AltTabWindow()
    {
        InitializeComponent();
        Title = "Veil Alt Tab";
        Activated += OnFirstActivated;
        Closed += OnClosed;
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        _hwnd = WindowHelper.GetHwnd(this);
        WindowHelper.RemoveTitleBar(this);
        WindowHelper.MakeOverlay(this);
        SetupAcrylic(global::Windows.UI.Color.FromArgb(255, 24, 28, 36));
        ShowWindowNative(_hwnd, SW_HIDE);
    }

    private void SetupAcrylic(global::Windows.UI.Color accentColor)
    {
        _acrylicController?.Dispose();

        bool useDarkForeground = UseDarkForeground(accentColor);
        global::Windows.UI.Color tintColor = useDarkForeground
            ? Blend(accentColor, global::Windows.UI.Color.FromArgb(255, 255, 255, 255), 0.82)
            : Blend(accentColor, global::Windows.UI.Color.FromArgb(255, 10, 12, 18), 0.20);

        _acrylicController = new DesktopAcrylicController
        {
            TintColor = tintColor,
            TintOpacity = useDarkForeground ? 0.08f : 0.44f,
            LuminosityOpacity = useDarkForeground ? 0.82f : 0.22f,
            FallbackColor = global::Windows.UI.Color.FromArgb(useDarkForeground ? (byte)230 : (byte)214, tintColor.R, tintColor.G, tintColor.B)
        };

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = false,
            Theme = useDarkForeground ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark
        };

        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
    }

    internal void Initialize()
    {
        var appWindow = WindowHelper.GetAppWindow(this);
        appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(-9999, -9999, 1, 1));
        Activate();
    }

    internal void ShowSwitcher(IReadOnlyList<WindowSwitchEntry> entries, int selectedIndex, ScreenBounds displayBounds)
    {
        if (_hwnd == IntPtr.Zero || entries.Count == 0 || selectedIndex < 0 || selectedIndex >= entries.Count)
        {
            return;
        }

        WindowSwitchEntry selectedEntry = entries[selectedIndex];
        bool useDarkForeground = UseDarkForeground(selectedEntry.AccentColor);
        global::Windows.UI.Color foregroundColor = useDarkForeground
            ? global::Windows.UI.Color.FromArgb(255, 24, 28, 34)
            : global::Windows.UI.Color.FromArgb(255, 255, 255, 255);
        global::Windows.UI.Color secondaryForegroundColor = useDarkForeground
            ? global::Windows.UI.Color.FromArgb(196, 24, 28, 34)
            : global::Windows.UI.Color.FromArgb(198, 255, 255, 255);
        global::Windows.UI.Color subtleForegroundColor = useDarkForeground
            ? global::Windows.UI.Color.FromArgb(152, 24, 28, 34)
            : global::Windows.UI.Color.FromArgb(146, 255, 255, 255);
        global::Windows.UI.Color borderColor = global::Windows.UI.Color.FromArgb(
            useDarkForeground ? (byte)52 : (byte)38,
            foregroundColor.R,
            foregroundColor.G,
            foregroundColor.B);
        global::Windows.UI.Color panelColor = global::Windows.UI.Color.FromArgb(
            useDarkForeground ? (byte)104 : (byte)116,
            selectedEntry.AccentColor.R,
            selectedEntry.AccentColor.G,
            selectedEntry.AccentColor.B);

        SetupAcrylic(selectedEntry.AccentColor);

        PanelBorder.Width = Math.Clamp(displayBounds.Right - displayBounds.Left - 72, MinimumPanelWidth, DefaultPanelWidth);
        int panelHeight = Math.Clamp(displayBounds.Bottom - displayBounds.Top - 160, MinimumPanelHeight, DefaultPanelHeight);
        PanelBorder.Height = panelHeight;
        PanelBorder.Background = new SolidColorBrush(panelColor);
        PanelBorder.BorderBrush = new SolidColorBrush(borderColor);

        SelectedWindowTitleText.Text = selectedEntry.AppName;
        SelectedWindowSubtitleText.Text = selectedEntry.WindowTitle;
        SelectionText.Text = $"{selectedIndex + 1} of {entries.Count}";

        SelectedWindowTitleText.Foreground = new SolidColorBrush(foregroundColor);
        SelectedWindowSubtitleText.Foreground = new SolidColorBrush(secondaryForegroundColor);
        SelectionText.Foreground = new SolidColorBrush(subtleForegroundColor);
        HintText.Foreground = new SolidColorBrush(subtleForegroundColor);

        BuildItems(entries, selectedIndex, foregroundColor, secondaryForegroundColor, subtleForegroundColor, useDarkForeground);

        int width = (int)Math.Round(PanelBorder.Width);
        int height = panelHeight;
        int x = displayBounds.Left + Math.Max(0, ((displayBounds.Right - displayBounds.Left) - width) / 2);
        int y = displayBounds.Top + Math.Max(36, ((displayBounds.Bottom - displayBounds.Top) - height) / 2);

        var appWindow = WindowHelper.GetAppWindow(this);
        appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(x, y, width, height));
        WindowHelper.ApplyRoundedRegion(_hwnd, width, height, PanelCornerRadius);
        appWindow.Show();
        WindowHelper.SetAlwaysOnTop(this);
        _isVisible = true;

        PanelBorder.UpdateLayout();
        ScrollSelectionIntoView(selectedIndex, entries.Count);
    }

    internal void HideSwitcher()
    {
        if (!_isVisible || _hwnd == IntPtr.Zero)
        {
            return;
        }

        ShowWindowNative(_hwnd, SW_HIDE);
        _isVisible = false;
    }

    private void BuildItems(
        IReadOnlyList<WindowSwitchEntry> entries,
        int selectedIndex,
        global::Windows.UI.Color foregroundColor,
        global::Windows.UI.Color secondaryForegroundColor,
        global::Windows.UI.Color subtleForegroundColor,
        bool useDarkForeground)
    {
        ItemsPanel.Children.Clear();

        for (int index = 0; index < entries.Count; index++)
        {
            WindowSwitchEntry entry = entries[index];
            bool isSelected = index == selectedIndex;

            global::Windows.UI.Color cardBackground = isSelected
                ? global::Windows.UI.Color.FromArgb(useDarkForeground ? (byte)150 : (byte)62, 255, 255, 255)
                : global::Windows.UI.Color.FromArgb(useDarkForeground ? (byte)88 : (byte)28, 255, 255, 255);
            global::Windows.UI.Color cardBorder = isSelected
                ? global::Windows.UI.Color.FromArgb(useDarkForeground ? (byte)168 : (byte)88, 255, 255, 255)
                : global::Windows.UI.Color.FromArgb(useDarkForeground ? (byte)86 : (byte)38, 255, 255, 255);
            global::Windows.UI.Color previewBackground = isSelected
                ? global::Windows.UI.Color.FromArgb(useDarkForeground ? (byte)120 : (byte)40, 255, 255, 255)
                : global::Windows.UI.Color.FromArgb(useDarkForeground ? (byte)64 : (byte)20, 255, 255, 255);

            var card = new Border
            {
                Width = CardWidth,
                Height = CardHeight,
                CornerRadius = new CornerRadius(CardRadius),
                Padding = new Thickness(10),
                BorderThickness = isSelected ? new Thickness(1.5) : new Thickness(1),
                Background = new SolidColorBrush(cardBackground),
                BorderBrush = new SolidColorBrush(cardBorder),
                Opacity = isSelected ? 1 : 0.92,
                Margin = new Thickness(0, isSelected ? 0 : 8, 0, isSelected ? 8 : 0)
            };

            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new Grid
            {
                Margin = new Thickness(0, 0, 0, 8)
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconBadge = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(entry.AccentColor),
                VerticalAlignment = VerticalAlignment.Center
            };
            iconBadge.Child = new TextBlock
            {
                Text = GetMonogram(entry.AppName),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 10,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(230, 255, 255, 255))
            };

            Grid.SetColumn(iconBadge, 0);
            header.Children.Add(iconBadge);

            var appTitle = new TextBlock
            {
                Text = entry.AppName,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
                Foreground = new SolidColorBrush(foregroundColor),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(appTitle, 1);
            header.Children.Add(appTitle);

            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(isSelected ? global::Windows.UI.Color.FromArgb(220, 255, 255, 255) : subtleForegroundColor),
                Opacity = isSelected ? 1 : 0.42
            };
            Grid.SetColumn(dot, 2);
            header.Children.Add(dot);

            Grid.SetRow(header, 0);
            layout.Children.Add(header);

            var preview = new Border
            {
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(previewBackground),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(useDarkForeground ? (byte)42 : (byte)20, 255, 255, 255))
            };

            var previewLayout = new Grid
            {
                Margin = new Thickness(8)
            };
            previewLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            previewLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            previewLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var chrome = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4
            };
            chrome.Children.Add(CreateChromeDot(236, 93, 82));
            chrome.Children.Add(CreateChromeDot(245, 190, 74));
            chrome.Children.Add(CreateChromeDot(98, 196, 84));
            Grid.SetRow(chrome, 0);
            previewLayout.Children.Add(chrome);

            var titleLines = new StackPanel
            {
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleLines.Children.Add(new Border
            {
                Width = 112,
                Height = 8,
                CornerRadius = new CornerRadius(999),
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(useDarkForeground ? (byte)104 : (byte)42, 255, 255, 255))
            });
            titleLines.Children.Add(new Border
            {
                Width = 78,
                Height = 6,
                CornerRadius = new CornerRadius(999),
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(useDarkForeground ? (byte)82 : (byte)28, 255, 255, 255))
            });
            Grid.SetRow(titleLines, 1);
            previewLayout.Children.Add(titleLines);

            var windowTitle = new TextBlock
            {
                Text = entry.WindowTitle,
                FontSize = 10,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
                Foreground = new SolidColorBrush(secondaryForegroundColor),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(windowTitle, 2);
            previewLayout.Children.Add(windowTitle);

            preview.Child = previewLayout;
            Grid.SetRow(preview, 1);
            layout.Children.Add(preview);

            card.Child = layout;
            ItemsPanel.Children.Add(card);
        }
    }

    private static Ellipse CreateChromeDot(byte red, byte green, byte blue)
    {
        return new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(global::Windows.UI.Color.FromArgb(210, red, green, blue))
        };
    }

    private static string GetMonogram(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "A";
        }

        return value.Trim()[0].ToString().ToUpperInvariant();
    }

    private void ScrollSelectionIntoView(int selectedIndex, int entryCount)
    {
        double viewportWidth = ItemsScrollViewer.ActualWidth;
        if (viewportWidth <= 0)
        {
            return;
        }

        double cardPitch = CardWidth + CardSpacing;
        double extentWidth = (entryCount * cardPitch) - CardSpacing;
        double centeredOffset = (selectedIndex * cardPitch) - ((viewportWidth - CardWidth) / 2);
        double targetOffset = Math.Clamp(centeredOffset, 0, Math.Max(0, extentWidth - viewportWidth));
        ItemsScrollViewer.ChangeView(targetOffset, null, null, true);
    }

    private static global::Windows.UI.Color Blend(global::Windows.UI.Color baseColor, global::Windows.UI.Color targetColor, double amount)
    {
        double clampedAmount = Math.Clamp(amount, 0, 1);
        return global::Windows.UI.Color.FromArgb(
            255,
            (byte)Math.Round(baseColor.R + ((targetColor.R - baseColor.R) * clampedAmount)),
            (byte)Math.Round(baseColor.G + ((targetColor.G - baseColor.G) * clampedAmount)),
            (byte)Math.Round(baseColor.B + ((targetColor.B - baseColor.B) * clampedAmount)));
    }

    private static bool UseDarkForeground(global::Windows.UI.Color color)
    {
        double luminance = (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);
        return luminance >= 168;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _acrylicController?.Dispose();
        _acrylicController = null;
    }
}
