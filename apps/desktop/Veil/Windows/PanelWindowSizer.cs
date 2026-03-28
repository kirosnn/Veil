using Microsoft.UI.Xaml;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

internal readonly record struct PanelWindowMetrics(
    int Width,
    int Height,
    bool IsHeightClamped,
    global::Windows.Graphics.RectInt32 Bounds);

internal static class PanelWindowSizer
{
    internal static PanelWindowMetrics Measure(
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
        int availableHeight = Math.Max(minHeight, workArea.Bottom - boundedAnchorY - screenMargin);
        int measurementWidth = Math.Min(preferredWidth, availableWidth);

        shell.Width = double.NaN;
        shell.MaxWidth = measurementWidth;
        shell.Measure(new global::Windows.Foundation.Size(measurementWidth, availableHeight));

        int finalWidth = Math.Clamp(
            (int)Math.Ceiling(shell.DesiredSize.Width) + widthPadding,
            Math.Min(minWidth, availableWidth),
            availableWidth);

        shell.Width = finalWidth;
        shell.MaxWidth = finalWidth;
        shell.Measure(new global::Windows.Foundation.Size(finalWidth, availableHeight));

        int desiredHeight = (int)Math.Ceiling(shell.DesiredSize.Height) + heightPadding;
        int finalHeight = Math.Clamp(
            desiredHeight,
            minHeight,
            availableHeight);

        shell.Height = finalHeight;

        int desiredLeft = anchorRight - finalWidth;
        int finalX = Math.Clamp(desiredLeft, workArea.Left + screenMargin, workArea.Right - finalWidth - screenMargin);
        int finalY = Math.Clamp(anchorY, workArea.Top + screenMargin, workArea.Bottom - finalHeight - screenMargin);

        return new PanelWindowMetrics(
            finalWidth,
            finalHeight,
            desiredHeight > availableHeight,
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
