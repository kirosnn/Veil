using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Veil.Diagnostics;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed class DesktopContextMenuService : IDisposable
{
    private const int MenuIdSettings = 2020;

    private IntPtr _mouseHook;
    private IntPtr _messageHwnd;
    private LowLevelMouseProc? _mouseProc;
    private WndProc? _wndProcDelegate;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _disposed;
    private bool _menuOpen;

    public event Action? SettingsRequested;

    public DesktopContextMenuService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public void Initialize()
    {
        _messageHwnd = CreateMessageWindow();
        _mouseProc = MouseHookCallback;
        _mouseHook = SetWindowsHookExW(
            WH_MOUSE_LL,
            _mouseProc,
            GetModuleHandleW(null),
            0);
    }

    private IntPtr CreateMessageWindow()
    {
        var hInstance = GetModuleHandleW(null);
        _wndProcDelegate = WndProcHandler;
        const string className = "VeilDesktopMenuWndClass";

        var wcex = new WndClassExW
        {
            cbSize = (uint)Marshal.SizeOf<WndClassExW>(),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance,
            lpszClassName = className
        };

        RegisterClassExW(ref wcex);

        return CreateWindowExW(
            0, className, "Veil Desktop Menu",
            0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    private IntPtr WndProcHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_COMMAND)
        {
            int menuId = (int)(wParam & 0xFFFF);
            switch (menuId)
            {
                case MenuIdSettings:
                    SettingsRequested?.Invoke();
                    break;
            }

            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == WM_RBUTTONUP && !_menuOpen)
        {
            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (IsClickOnDesktop(hookStruct.pt))
            {
                _dispatcherQueue.TryEnqueue(() => ShowContextMenu(hookStruct.pt));
                return new IntPtr(1); // Suppress default context menu
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static bool IsClickOnDesktop(Point pt)
    {
        IntPtr hwndAtPoint = WindowFromPoint(pt);
        if (hwndAtPoint == IntPtr.Zero)
        {
            return false;
        }

        return IsDesktopWindow(hwndAtPoint);
    }

    private static bool IsDesktopWindow(IntPtr hwnd)
    {
        string className = GetWindowClass(hwnd);

        // Direct desktop windows
        if (className is "WorkerW" or "Progman" or "SysListView32" or "SHELLDLL_DefView")
        {
            return true;
        }

        // Check parent chain
        IntPtr parent = GetParent(hwnd);
        if (parent != IntPtr.Zero)
        {
            string parentClass = GetWindowClass(parent);
            if (parentClass is "WorkerW" or "Progman" or "SHELLDLL_DefView")
            {
                return true;
            }
        }

        return false;
    }

    private static string GetWindowClass(IntPtr hwnd)
    {
        var buffer = new char[256];
        int length = GetClassNameW(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private void ShowContextMenu(Point pt)
    {
        var hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
        {
            return;
        }

        _menuOpen = true;

        try
        {
            AppendMenuW(hMenu, MF_STRING, (nuint)MenuIdSettings, "Settings");

            SetForegroundWindow(_messageHwnd);

            int cmd = TrackPopupMenuEx(hMenu,
                TPM_RETURNCMD | TPM_NONOTIFY | TPM_LEFTALIGN | TPM_TOPALIGN,
                pt.X, pt.Y, _messageHwnd, IntPtr.Zero);

            if (cmd > 0)
            {
                PostMessageW(_messageHwnd, WM_COMMAND, (IntPtr)cmd, IntPtr.Zero);
            }
        }
        finally
        {
            DestroyMenu(hMenu);
            _menuOpen = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_messageHwnd != IntPtr.Zero)
        {
            WndProc? wndProcDelegate = _wndProcDelegate;
            DestroyWindow(_messageHwnd);
            GC.KeepAlive(wndProcDelegate);
            _messageHwnd = IntPtr.Zero;
        }

        _wndProcDelegate = null;
    }
}
