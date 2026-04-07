using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed class WindowsDesktopIconVisibilityBridge : IDesktopIconVisibilityBridge
{
    private const int ToggleDesktopIconsCommandId = 0x7402;

    public bool AreDesktopIconsHidden()
    {
        IntPtr desktopListView = FindDesktopListView();
        if (desktopListView == IntPtr.Zero)
        {
            return false;
        }

        return !IsWindowVisible(desktopListView);
    }

    public void SetDesktopIconsHidden(bool hidden)
    {
        bool currentlyHidden = AreDesktopIconsHidden();
        if (currentlyHidden == hidden)
        {
            return;
        }

        IntPtr desktopDefView = FindDesktopDefView();
        if (desktopDefView == IntPtr.Zero)
        {
            return;
        }

        PostMessageW(desktopDefView, WM_COMMAND, (IntPtr)ToggleDesktopIconsCommandId, IntPtr.Zero);
    }

    private static IntPtr FindDesktopDefView()
    {
        IntPtr shellWindow = GetShellWindow();
        if (shellWindow != IntPtr.Zero)
        {
            IntPtr defView = FindWindowExW(shellWindow, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                return defView;
            }
        }

        IntPtr desktopDefView = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            IntPtr defView = FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero)
            {
                return true;
            }

            desktopDefView = defView;
            return false;
        }, IntPtr.Zero);

        return desktopDefView;
    }

    private static IntPtr FindDesktopListView()
    {
        IntPtr defView = FindDesktopDefView();
        if (defView == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return FindWindowExW(defView, IntPtr.Zero, "SysListView32", null);
    }
}
