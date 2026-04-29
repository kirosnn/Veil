using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal static class FullscreenDetectionService
{
    internal static bool IsFullscreenWindowOnMonitor(ScreenBounds screen, IntPtr excludeHwnd)
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == excludeHwnd)
        {
            return false;
        }

        if (IsIconic(hwnd))
        {
            return false;
        }

        // Skip windows cloaked by DWM (virtual desktops, UWP background apps)
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out uint cloaked, sizeof(uint)) == 0 && cloaked != 0)
        {
            return false;
        }

        if (!GetWindowRect(hwnd, out Rect rect))
        {
            return false;
        }

        return rect.Left <= screen.Left
            && rect.Top <= screen.Top
            && rect.Right >= screen.Right
            && rect.Bottom >= screen.Bottom;
    }
}
