using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Veil.Interop;
using Veil.Services;
using WinRT;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class AltTabWindow : Window
{
    private const int DefaultPanelWidth = 1080;
    private const int DefaultPanelHeight = 560;
    private const int MinimumPanelWidth = 760;
    private const int MinimumPanelHeight = 440;
    private const int PanelCornerRadius = 24;
    private const int CardWidth = 224;
    private const int CardHeight = 92;
    private const int CardRadius = 22;
    private const int CardTextWidth = 160;
    private const int CardSpacing = 12;

    private IntPtr _hwnd;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private IntPtr _thumbnailHandle;
    private IntPtr _thumbnailSourceWindow;
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
        SetupAcrylic(global::Windows.UI.Color.FromArgb(255, 18, 18, 22));
        ShowWindowNative(_hwnd, SW_HIDE);
    }

    private void SetupAcrylic(global::Windows.UI.Color accentColor)
    {
        _acrylicController?.Dispose();

        bool useDarkForeground = UseDarkForeground(accentColor);
        global::Windows.UI.Color tintColor = useDarkForeground
            ? Blend(accentColor, global::Windows.UI.Color.FromArgb(255, 255, 255, 255), 0.76)
            : Blend(accentColor, global::Windows.UI.Color.FromArgb(255, 8, 8, 10), 0.18);

        _acrylicController = new DesktopAcrylicController
        {
            TintColor = tintColor,
            TintOpacity = useDarkForeground ? 0.10f : 0.54f,
            LuminosityOpacity = useDarkForeground ? 0.78f : 0.24f,
            FallbackColor = global::Windows.UI.Color.FromArgb(useDarkForeground ? (byte)224 : (byte)236, tintColor.R, tintColor.G, tintColor.B)
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
            ? global::Windows.UI.Color.FromArgb(255, 20, 20, 24)
            : global::Windows.UI.Color.FromArgb(255, 255, 255, 255);
        global::Windows.UI.Color secondaryForegroundColor = useDarkForeground
            ? global::Windows.UI.Color.FromArgb(214, 20, 20, 24)
            : global::Windows.UI.Color.FromArgb(190, 255, 255, 255);
        global::Windows.UI.Color subtleForegroundColor = useDarkForeground
            ? global::Windows.UI.Color.FromArgb(156, 20, 20, 24)
            : global::Windows.UI.Color.FromArgb(136, 255, 255, 255);
        global::Windows.UI.Color borderColor = global::Windows.UI.Color.FromArgb(
            useDarkForeground ? (byte)54 : (byte)42,
            foregroundColor.R,
            foregroundColor.G,
            foregroundColor.B);
        global::Windows.UI.Color softSurfaceColor = global::Windows.UI.Color.FromArgb(
            useDarkForeground ? (byte)38 : (byte)24,
            foregroundColor.R,
            foregroundColor.G,
            foregroundColor.B);

        SetupAcrylic(selectedEntry.AccentColor);

        PanelBorder.Width = Math.Clamp(displayBounds.Right - displayBounds.Left - 48, MinimumPanelWidth, DefaultPanelWidth);
        int panelHeight = Math.Clamp(displayBounds.Bottom - displayBounds.Top - 64, MinimumPanelHeight, DefaultPanelHeight);
        PanelBorder.Height = panelHeight;
        PreviewFrame.Height = Math.Max(220, panelHeight - 238);
        PreviewFrame.Width = PanelBorder.Width - 2;

        PanelBorder.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(
            useDarkForeground ? (byte)112 : (byte)132,
            selectedEntry.AccentColor.R,
            selectedEntry.AccentColor.G,
            selectedEntry.AccentColor.B));
        PanelBorder.BorderBrush = new SolidColorBrush(borderColor);
        PreviewFrame.Background = new SolidColorBrush(softSurfaceColor);
        PreviewFrame.BorderBrush = new SolidColorBrush(borderColor);
        PreviewFrame.BorderThickness = new Thickness(1);
        CountBadge.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(
            useDarkForeground ? (byte)28 : (byte)24,
            foregroundColor.R,
            foregroundColor.G,
            foregroundColor.B));

        TitleText.Foreground = new SolidColorBrush(foregroundColor);
        HintText.Foreground = new SolidColorBrush(subtleForegroundColor);
        WindowCountText.Foreground = new SolidColorBrush(secondaryForegroundColor);
        SelectedWindowTitleText.Foreground = new SolidColorBrush(foregroundColor);
        SelectedWindowSubtitleText.Foreground = new SolidColorBrush(secondaryForegroundColor);
        SelectionText.Foreground = new SolidColorBrush(secondaryForegroundColor);
        PreviewUnavailableText.Foreground = new SolidColorBrush(secondaryForegroundColor);

        WindowCountText.Text = entries.Count == 1 ? "1 window" : $"{entries.Count} windows";
        SelectedWindowTitleText.Text = selectedEntry.AppName;
        SelectedWindowSubtitleText.Text = selectedEntry.WindowTitle;
        SelectionText.Text = $"{selectedIndex + 1} / {entries.Count}";
        PreviewUnavailableText.Text = selectedEntry.IsMinimized
            ? "Minimized window preview unavailable"
            : "Live preview unavailable";

        BuildItems(entries, selectedIndex, foregroundColor, secondaryForegroundColor);

        int width = (int)Math.Round(PanelBorder.Width);
        int height = panelHeight;
        int x = displayBounds.Left + Math.Max(0, ((displayBounds.Right - displayBounds.Left) - width) / 2);
        int y = displayBounds.Top + Math.Max(20, ((displayBounds.Bottom - displayBounds.Top) - height) / 4);

        var appWindow = WindowHelper.GetAppWindow(this);
        appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(x, y, width, height));
        WindowHelper.ApplyRoundedRegion(_hwnd, width, height, PanelCornerRadius);
        appWindow.Show();
        WindowHelper.SetAlwaysOnTop(this);
        _isVisible = true;

        PanelBorder.UpdateLayout();
        ScrollSelectionIntoView(selectedIndex, entries.Count);
        UpdateThumbnail(selectedEntry.Handle);
    }

    internal void HideSwitcher()
    {
        if (!_isVisible || _hwnd == IntPtr.Zero)
        {
            return;
        }

        ClearThumbnail();
        ShowWindowNative(_hwnd, SW_HIDE);
        _isVisible = false;
    }

    private void BuildItems(
        IReadOnlyList<WindowSwitchEntry> entries,
        int selectedIndex,
        global::Windows.UI.Color foregroundColor,
        global::Windows.UI.Color secondaryForegroundColor)
    {
        ItemsPanel.Children.Clear();

        for (int index = 0; index < entries.Count; index++)
        {
            WindowSwitchEntry entry = entries[index];
            bool isSelected = index == selectedIndex;

            var card = new Border
            {
                Width = CardWidth,
                Height = CardHeight,
                CornerRadius = new CornerRadius(CardRadius),
                Padding = new Thickness(14, 12, 14, 12),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(isSelected
                    ? global::Windows.UI.Color.FromArgb(82, foregroundColor.R, foregroundColor.G, foregroundColor.B)
                    : global::Windows.UI.Color.FromArgb(18, foregroundColor.R, foregroundColor.G, foregroundColor.B)),
                BorderBrush = new SolidColorBrush(isSelected
                    ? global::Windows.UI.Color.FromArgb(72, foregroundColor.R, foregroundColor.G, foregroundColor.B)
                    : global::Windows.UI.Color.FromArgb(24, foregroundColor.R, foregroundColor.G, foregroundColor.B)),
                Opacity = isSelected ? 1 : 0.94
            };

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var accentPill = new Border
            {
                Width = 6,
                Height = 44,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(entry.AccentColor),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(accentPill, 0);
            layout.Children.Add(accentPill);

            var textPanel = new StackPanel
            {
                Margin = new Thickness(12, 0, 0, 0),
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center
            };

            textPanel.Children.Add(new TextBlock
            {
                Text = entry.AppName,
                MaxWidth = CardTextWidth,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 12,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
                Foreground = new SolidColorBrush(foregroundColor)
            });

            textPanel.Children.Add(new TextBlock
            {
                Text = entry.WindowTitle,
                MaxWidth = CardTextWidth,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 11,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
                Foreground = new SolidColorBrush(secondaryForegroundColor)
            });

            Grid.SetColumn(textPanel, 1);
            layout.Children.Add(textPanel);

            card.Child = layout;
            ItemsPanel.Children.Add(card);
        }
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

    private void UpdateThumbnail(IntPtr sourceWindow)
    {
        ClearThumbnail();

        PreviewUnavailableText.Visibility = Visibility.Visible;

        if (sourceWindow == IntPtr.Zero || sourceWindow == _hwnd)
        {
            return;
        }

        if (DwmRegisterThumbnail(_hwnd, sourceWindow, out IntPtr thumbnailHandle) != 0 || thumbnailHandle == IntPtr.Zero)
        {
            return;
        }

        _thumbnailHandle = thumbnailHandle;
        _thumbnailSourceWindow = sourceWindow;

        if (DwmQueryThumbnailSourceSize(_thumbnailHandle, out NativeSize sourceSize) != 0 || sourceSize.Width <= 0 || sourceSize.Height <= 0)
        {
            ClearThumbnail();
            return;
        }

        Rect destination = CalculatePreviewDestinationRect(sourceSize);
        var props = new DwmThumbnailProperties
        {
            dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_OPACITY | DWM_TNP_VISIBLE | DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = destination,
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = false
        };

        if (DwmUpdateThumbnailProperties(_thumbnailHandle, ref props) == 0)
        {
            PreviewUnavailableText.Visibility = Visibility.Collapsed;
            return;
        }

        ClearThumbnail();
    }

    private Rect CalculatePreviewDestinationRect(NativeSize sourceSize)
    {
        if (Content is not UIElement contentRoot)
        {
            return default;
        }

        global::Windows.Foundation.Point origin = PreviewFrame.TransformToVisual(contentRoot).TransformPoint(new global::Windows.Foundation.Point(0, 0));
        int availableWidth = Math.Max(1, (int)Math.Round(PreviewFrame.ActualWidth));
        int availableHeight = Math.Max(1, (int)Math.Round(PreviewFrame.ActualHeight));

        double widthScale = availableWidth / (double)sourceSize.Width;
        double heightScale = availableHeight / (double)sourceSize.Height;
        double scale = Math.Min(widthScale, heightScale);

        int renderWidth = Math.Max(1, (int)Math.Round(sourceSize.Width * scale));
        int renderHeight = Math.Max(1, (int)Math.Round(sourceSize.Height * scale));
        int offsetX = (int)Math.Round(origin.X) + Math.Max(0, (availableWidth - renderWidth) / 2);
        int offsetY = (int)Math.Round(origin.Y) + Math.Max(0, (availableHeight - renderHeight) / 2);

        return new Rect
        {
            Left = offsetX,
            Top = offsetY,
            Right = offsetX + renderWidth,
            Bottom = offsetY + renderHeight
        };
    }

    private void ClearThumbnail()
    {
        if (_thumbnailHandle != IntPtr.Zero)
        {
            DwmUnregisterThumbnail(_thumbnailHandle);
            _thumbnailHandle = IntPtr.Zero;
            _thumbnailSourceWindow = IntPtr.Zero;
        }
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
        ClearThumbnail();
        _acrylicController?.Dispose();
        _acrylicController = null;
    }
}
