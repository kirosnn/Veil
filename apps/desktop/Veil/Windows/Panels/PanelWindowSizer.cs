using Microsoft.UI.Xaml;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

internal readonly record struct PanelWindowMetrics(
    int Width,
    int Height,
    double ViewWidth,
    double ViewHeight,
    bool IsWidthClamped,
    bool IsHeightClamped,
    global::Windows.Graphics.RectInt32 Bounds);

internal static class PanelWindowSizer
{
    internal static PanelWindowMetrics Measure(
        Window window,
        FrameworkElement shell,
        int anchorRight,
        int anchorY,
        int preferredWidth,
        int minWidth,
        int minHeight,
        int widthPadding,
        int heightPadding,
        int screenMargin = 8)
    {
        NativeMethods.Rect workArea = ResolveWorkArea(anchorRight, anchorY);
        int boundedAnchorY = Math.Clamp(anchorY, workArea.Top + screenMargin, workArea.Bottom - screenMargin);
        int availableWidth = Math.Max(1, workArea.Right - workArea.Left - (screenMargin * 2));
        int availableHeight = Math.Max(1, workArea.Bottom - boundedAnchorY - screenMargin);
        double availableWidthView = Math.Max(1, WindowHelper.PhysicalPixelsToView(window, availableWidth));
        double availableHeightView = Math.Max(1, WindowHelper.PhysicalPixelsToView(window, availableHeight));
        double measurementWidth = Math.Min(preferredWidth, availableWidthView);

        shell.Width = double.NaN;
        shell.MaxWidth = measurementWidth;
        shell.Height = double.NaN;
        shell.Measure(new global::Windows.Foundation.Size(measurementWidth, availableHeightView));

        double desiredWidth = Math.Ceiling(shell.DesiredSize.Width) + widthPadding;
        double finalWidthView = Math.Clamp(
            desiredWidth,
            Math.Min(minWidth, availableWidthView),
            availableWidthView);

        shell.Width = finalWidthView;
        shell.MaxWidth = finalWidthView;
        shell.Measure(new global::Windows.Foundation.Size(finalWidthView, availableHeightView));

        double desiredHeight = Math.Ceiling(shell.DesiredSize.Height) + heightPadding;
        double finalHeightView = Math.Clamp(
            desiredHeight,
            minHeight,
            availableHeightView);

        int finalWidth = Math.Max(1, WindowHelper.ViewPixelsToPhysical(window, finalWidthView));
        int finalHeight = Math.Max(1, WindowHelper.ViewPixelsToPhysical(window, finalHeightView));

        shell.Height = finalHeightView;

        int desiredLeft = anchorRight - finalWidth;
        int finalX = Math.Clamp(desiredLeft, workArea.Left + screenMargin, workArea.Right - finalWidth - screenMargin);
        int finalY = Math.Clamp(anchorY, workArea.Top + screenMargin, workArea.Bottom - finalHeight - screenMargin);

        return new PanelWindowMetrics(
            finalWidth,
            finalHeight,
            finalWidthView,
            finalHeightView,
            finalWidthView < preferredWidth,
            finalHeightView < desiredHeight,
            new global::Windows.Graphics.RectInt32(finalX, finalY, finalWidth, finalHeight));
    }

    internal static NativeMethods.Rect ResolveWorkArea(int x, int y)
    {
        foreach (MonitorInfo2 monitor in MonitorService.GetAllMonitors())
        {
            NativeMethods.Rect area = monitor.WorkArea;
            if (x >= area.Left && x < area.Right && y >= area.Top && y < area.Bottom)
            {
                return area;
            }
        }

        return MonitorService.GetAllMonitors()
            .OrderByDescending(static monitor => monitor.IsPrimary)
            .Select(static monitor => monitor.WorkArea)
            .FirstOrDefault();
    }
}
