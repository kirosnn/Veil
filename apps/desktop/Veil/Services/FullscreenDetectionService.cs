using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal static class FullscreenDetectionService
{
    internal static bool IsFullscreenWindowOnMonitor(ScreenBounds screen, IntPtr excludeHwnd)
    {
        if (!WindowHelper.TryGetForegroundContentWindow(screen, excludeHwnd, out IntPtr hwnd, out Rect rect))
        {
            return false;
        }

        if (IsZoomed(hwnd))
        {
            return false;
        }

        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out uint cloaked, sizeof(uint)) == 0 && cloaked != 0)
        {
            return false;
        }

        const int tolerance = 3;

        return CoversBounds(rect, screen.Left, screen.Top, screen.Right, screen.Bottom, tolerance);
    }

    private static bool CoversBounds(Rect rect, int left, int top, int right, int bottom, int tolerance)
    {
        return rect.Left <= left + tolerance
            && rect.Top <= top + tolerance
            && rect.Right >= right - tolerance
            && rect.Bottom >= bottom - tolerance;
    }

    private static bool TryGetVisibleWindowRect(IntPtr hwnd, out Rect rect)
    {
        if (DwmGetWindowAttributeRect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, System.Runtime.InteropServices.Marshal.SizeOf<Rect>()) == 0
            && rect.Right > rect.Left
            && rect.Bottom > rect.Top)
        {
            return true;
        }

        return GetWindowRect(hwnd, out rect);
    }
}
