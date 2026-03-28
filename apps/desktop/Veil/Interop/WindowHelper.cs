using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Interop;

internal static class WindowHelper
{
    internal static IntPtr GetHwnd(Window window)
    {
        return WindowNative.GetWindowHandle(window);
    }

    internal static AppWindow GetAppWindow(Window window)
    {
        var hwnd = GetHwnd(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    internal static void MakeOverlay(Window window)
    {
        var hwnd = GetHwnd(window);
        int exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        exStyle |= WS_EX_NOACTIVATE;
        SetWindowLongW(hwnd, GWL_EXSTYLE, exStyle);
    }

    internal static void DisableLayeredTransparency(Window window)
    {
        DisableLayeredTransparency(GetHwnd(window));
    }

    internal static void EnableLayeredTransparency(Window window)
    {
        EnableLayeredTransparency(GetHwnd(window));
    }

    internal static void EnableLayeredTransparency(IntPtr hwnd)
    {
        int exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_LAYERED;
        SetWindowLongW(hwnd, GWL_EXSTYLE, exStyle);
        SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
    }

    internal static void DisableLayeredTransparency(IntPtr hwnd)
    {
        int exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_LAYERED) == 0)
        {
            return;
        }

        exStyle &= ~WS_EX_LAYERED;
        SetWindowLongW(hwnd, GWL_EXSTYLE, exStyle);
    }

    internal static void SetAlwaysOnTop(Window window)
    {
        var hwnd = GetHwnd(window);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    internal static void RemoveTitleBar(Window window)
    {
        var appWindow = GetAppWindow(window);
        var presenter = appWindow.Presenter as OverlappedPresenter;
        if (presenter != null)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        var hwnd = GetHwnd(window);
        int style = GetWindowLongW(hwnd, GWL_STYLE);
        style &= ~WS_THICKFRAME;
        style &= ~WS_BORDER;
        style &= ~WS_CAPTION;
        style &= ~WS_DLGFRAME;
        SetWindowLongW(hwnd, GWL_STYLE, style);

        int cornerPref = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE,
            ref cornerPref, sizeof(int));

        int borderColor = unchecked((int)DWMWA_COLOR_NONE);
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR,
            ref borderColor, sizeof(int));

        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    internal static void ExtendFrameIntoClientArea(Window window)
    {
        var hwnd = GetHwnd(window);
        var margins = new Margins
        {
            cxLeftWidth = -1,
            cxRightWidth = -1,
            cyTopHeight = -1,
            cyBottomHeight = -1
        };

        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    internal static void PositionOnMonitor(Window window, int x, int y, int width, int height)
    {
        var appWindow = GetAppWindow(window);
        appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(x, y, width, height));
    }

    internal static void RegisterAppBar(Window window, ScreenBounds screen, uint edge, int barSize)
    {
        var hwnd = GetHwnd(window);

        var abd = new AppBarData
        {
            cbSize = Marshal.SizeOf<AppBarData>(),
            hWnd = hwnd,
            uEdge = edge,
            rc = new Rect
            {
                Left = screen.Left,
                Top = screen.Top,
                Right = screen.Right,
                Bottom = screen.Bottom
            }
        };

        if (edge == ABE_TOP)
        {
            abd.rc.Bottom = abd.rc.Top + barSize;
        }
        else if (edge == ABE_BOTTOM)
        {
            abd.rc.Top = abd.rc.Bottom - barSize;
        }

        SHAppBarMessage(ABM_NEW, ref abd);
        SHAppBarMessage(ABM_QUERYPOS, ref abd);
        SHAppBarMessage(ABM_SETPOS, ref abd);
    }

    internal static void UnregisterAppBar(Window window)
    {
        var hwnd = GetHwnd(window);
        var abd = new AppBarData
        {
            cbSize = Marshal.SizeOf<AppBarData>(),
            hWnd = hwnd
        };
        SHAppBarMessage(ABM_REMOVE, ref abd);
    }

    internal static void HideWindow(Window window)
    {
        var appWindow = GetAppWindow(window);
        appWindow.Hide();
    }

    internal static void ShowWindow(Window window)
    {
        var appWindow = GetAppWindow(window);
        appWindow.Show();
    }

    internal static void ApplyRoundedRegion(Window window, int width, int height, int radius)
    {
        var hwnd = GetHwnd(window);
        ApplyRoundedRegion(hwnd, width, height, radius);
    }

    internal static void ApplyRoundedRegion(IntPtr hwnd, int width, int height, int radius)
    {
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (region != IntPtr.Zero)
        {
            SetWindowRgn(hwnd, region, true);
        }
    }

    internal static void SetWindowIcon(Window window, string icoPath)
    {
        var appWindow = GetAppWindow(window);
        appWindow.SetIcon(icoPath);
    }

    internal static bool IsForegroundFullscreen(ScreenBounds screen, IntPtr excludeHwnd)
    {
        if (!TryGetForegroundContentWindow(screen, excludeHwnd, out _, out var windowRect))
            return false;

        const int tolerance = 2;

        return windowRect.Left <= screen.Left + tolerance
            && windowRect.Top <= screen.Top + tolerance
            && windowRect.Right >= screen.Right - tolerance
            && windowRect.Bottom >= screen.Bottom - tolerance;
    }

    internal static bool IsForegroundMaximizedOrFullscreen(ScreenBounds screen, IntPtr excludeHwnd)
    {
        if (!TryGetForegroundContentWindow(screen, excludeHwnd, out var fgHwnd, out var windowRect))
            return false;

        if (IsZoomed(fgHwnd))
            return true;

        const int tolerance = 2;

        return windowRect.Left <= screen.Left + tolerance
            && windowRect.Top <= screen.Top + tolerance
            && windowRect.Right >= screen.Right - tolerance
            && windowRect.Bottom >= screen.Bottom - tolerance;
    }

    internal static bool TryGetForegroundContentWindow(
        ScreenBounds screen,
        IntPtr excludeHwnd,
        out IntPtr foregroundWindow,
        out Rect windowRect)
    {
        foregroundWindow = GetForegroundWindow();
        windowRect = default;

        if (foregroundWindow == IntPtr.Zero || foregroundWindow == excludeHwnd)
            return false;

        if (IsDesktopShellWindow(foregroundWindow))
            return false;

        if (IsExplorerShellProcess(foregroundWindow))
            return false;

        if (!IsWindowVisible(foregroundWindow) || IsIconic(foregroundWindow))
            return false;

        if (!GetWindowRect(foregroundWindow, out windowRect))
            return false;

        bool intersectsScreen = windowRect.Right > screen.Left
            && windowRect.Left < screen.Right
            && windowRect.Bottom > screen.Top
            && windowRect.Top < screen.Bottom;

        return intersectsScreen;
    }

    private static bool IsDesktopShellWindow(IntPtr hwnd)
    {
        if (hwnd == GetShellWindow())
            return true;

        var className = GetWindowClassName(hwnd);
        if (string.IsNullOrEmpty(className))
            return false;

        if (className is "Progman" or "WorkerW")
            return true;

        if (className is "SHELLDLL_DefView" or "SysListView32")
            return true;

        return HasShellDesktopChild(hwnd);
    }

    private static bool HasShellDesktopChild(IntPtr hwnd)
    {
        var defView = FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero)
            return true;

        var listView = FindWindowExW(hwnd, IntPtr.Zero, "SysListView32", null);
        return listView != IntPtr.Zero;
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var classNameBuffer = new char[256];
        int classNameLength = GetClassNameW(hwnd, classNameBuffer, classNameBuffer.Length);
        if (classNameLength <= 0)
            return string.Empty;

        return new string(classNameBuffer, 0, classNameLength);
    }

    private static bool IsExplorerShellProcess(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0)
            return false;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
