using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class TopBarWindow
{
    private readonly record struct WindowRegionSegment(int Left, int Top, int Right, int Bottom);

    private void UpdateWindowRegion()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_settings.TopBarStyle != "Transparent")
        {
            if (_lastWindowRegionSignature.Length == 0)
            {
                UpdateShortcutGlassBlurRegion();
                return;
            }

            SetWindowRgn(_hwnd, IntPtr.Zero, true);
            _lastWindowRegionSignature = string.Empty;
            UpdateShortcutGlassBlurRegion();
            return;
        }

        IReadOnlyList<WindowRegionSegment> regions = CollectTransparentRegions();
        if (regions.Count == 0)
        {
            if (_lastWindowRegionSignature.Length == 0)
            {
                UpdateShortcutGlassBlurRegion();
                return;
            }

            SetWindowRgn(_hwnd, IntPtr.Zero, true);
            _lastWindowRegionSignature = string.Empty;
            UpdateShortcutGlassBlurRegion();
            return;
        }

        string signature = string.Join("|", regions.Select(static region =>
            $"{region.Left},{region.Top},{region.Right},{region.Bottom}"));
        if (string.Equals(signature, _lastWindowRegionSignature, StringComparison.Ordinal))
        {
            return;
        }

        IntPtr combinedRegion = CreateRectRgn(0, 0, 0, 0);
        if (combinedRegion == IntPtr.Zero)
        {
            return;
        }

        bool applied = false;
        try
        {
            foreach (WindowRegionSegment region in regions)
            {
                IntPtr segmentRegion = CreateRectRgn(region.Left, region.Top, region.Right, region.Bottom);
                if (segmentRegion == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    CombineRgn(combinedRegion, combinedRegion, segmentRegion, RGN_OR);
                    applied = true;
                }
                finally
                {
                    DeleteObject(segmentRegion);
                }
            }

            if (!applied)
            {
                return;
            }

            SetWindowRgn(_hwnd, combinedRegion, true);
            combinedRegion = IntPtr.Zero;
            _lastWindowRegionSignature = signature;
        }
        finally
        {
            if (combinedRegion != IntPtr.Zero)
            {
                DeleteObject(combinedRegion);
            }
        }

        UpdateShortcutGlassBlurRegion();
    }

    private IReadOnlyList<WindowRegionSegment> CollectTransparentRegions()
    {
        List<WindowRegionSegment> regions = [];

        AddContentRegion(regions, MenuButton, useElementBounds: true, paddingX: 1, paddingY: 1);
        AddContentRegion(regions, FinderButton, useElementBounds: true, paddingX: 8, paddingY: 5);
        AddContentRegion(regions, DiscordButton, useElementBounds: false, paddingX: 1, paddingY: 1);
        AddContentRegion(regions, MusicButton, useElementBounds: false, paddingX: 1, paddingY: 1);
        AddContentRegion(regions, RunCatButton, useElementBounds: false, paddingX: 1, paddingY: 1);
        AddElementRegion(regions, ClockText, paddingX: 1, paddingY: 1);
        AddElementRegion(regions, ShortcutGlass, paddingX: 0, paddingY: 0);

        // Terminal tab strip — include when visible so tabs remain interactive
        if (TerminalTabPanel.Visibility == Visibility.Visible)
        {
            AddElementRegion(regions, TerminalTabPanel, paddingX: 2, paddingY: 2);
        }

        foreach (Button shortcutButton in _shortcutButtons)
        {
            AddContentRegion(regions, shortcutButton, useElementBounds: true, paddingX: 8, paddingY: 5);
        }

        return regions;
    }

    private void AddContentRegion(List<WindowRegionSegment> regions, Button button, bool useElementBounds, double paddingX, double paddingY)
    {
        if (button.Visibility != Visibility.Visible)
        {
            return;
        }

        FrameworkElement target = useElementBounds
            ? button
            : button.Content as FrameworkElement ?? button;

        AddElementRegion(regions, target, paddingX, paddingY);
    }

    private void AddElementRegion(List<WindowRegionSegment> regions, FrameworkElement element, double paddingX, double paddingY)
    {
        if (!TryCreateWindowRegionSegment(element, paddingX, paddingY, out WindowRegionSegment region))
        {
            return;
        }

        regions.Add(region);
    }

    private bool TryCreateWindowRegionSegment(FrameworkElement element, double paddingX, double paddingY, out WindowRegionSegment region)
    {
        region = default;

        if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            GeneralTransform transform = element.TransformToVisual(RootPanel);
            var topLeft = transform.TransformPoint(new global::Windows.Foundation.Point(0, 0));
            var bottomRight = transform.TransformPoint(new global::Windows.Foundation.Point(element.ActualWidth, element.ActualHeight));
            double minX = Math.Min(topLeft.X, bottomRight.X) - paddingX;
            double minY = Math.Min(topLeft.Y, bottomRight.Y) - paddingY;
            double maxX = Math.Max(topLeft.X, bottomRight.X) + paddingX;
            double maxY = Math.Max(topLeft.Y, bottomRight.Y) + paddingY;

            int rootWidth = Math.Max(1, WindowHelper.ViewPixelsToPhysical(this, RootPanel.ActualWidth));
            int rootHeight = Math.Max(1, WindowHelper.ViewPixelsToPhysical(this, RootPanel.ActualHeight));
            int left = Math.Max(0, WindowHelper.ViewPixelsToPhysical(this, minX));
            int top = Math.Max(0, WindowHelper.ViewPixelsToPhysical(this, minY));
            int right = Math.Min(rootWidth, WindowHelper.ViewPixelsToPhysical(this, maxX));
            int bottom = Math.Min(rootHeight, WindowHelper.ViewPixelsToPhysical(this, maxY));

            if (right <= left || bottom <= top)
            {
                return false;
            }

            region = new WindowRegionSegment(left, top, right, bottom);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
