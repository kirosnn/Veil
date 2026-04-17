using System.Runtime.InteropServices;
using Veil.Diagnostics;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed class TrayIconService : IDisposable
{
    private const uint TrayIconId = 1;
    private const int MenuIdShow = 1001;
    private const int MenuIdSettings = 1002;
    private const int MenuIdQuit = 1003;

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private NotifyIconData _nid;
    private WndProc? _wndProcDelegate;
    private bool _disposed;

    public event Action? ShowRequested;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;

    public void Initialize()
    {
        _hIcon = LoadAppIcon();
        _hwnd = CreateMessageWindow();
        AddTrayIcon();
        AppLogger.Info("Tray icon initialized.");
    }

    private IntPtr LoadAppIcon()
    {
        var exePath = Environment.ProcessPath ?? "Veil.exe";
        var icon = ExtractIconW(IntPtr.Zero, exePath, 0);
        return icon != IntPtr.Zero ? icon : IntPtr.Zero;
    }

    private IntPtr CreateMessageWindow()
    {
        var hInstance = GetModuleHandleW(null);
        _wndProcDelegate = WndProcHandler;
        var className = "VeilTrayWndClass";

        var wcex = new WndClassExW
        {
            cbSize = (uint)Marshal.SizeOf<WndClassExW>(),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance,
            lpszClassName = className
        };

        RegisterClassExW(ref wcex);

        var hwnd = CreateWindowExW(
            0, className, "Veil Tray",
            0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, hInstance, IntPtr.Zero);

        return hwnd;
    }

    private void AddTrayIcon()
    {
        _nid = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = (uint)WM_TRAYICON,
            hIcon = _hIcon,
            szTip = "Veil"
        };

        Shell_NotifyIconW(NIM_ADD, ref _nid);
    }

    private IntPtr WndProcHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            int eventId = (int)(lParam & 0xFFFF);

            if (eventId == WM_LBUTTONUP)
            {
                ShowRequested?.Invoke();
            }
            else if (eventId == WM_RBUTTONUP)
            {
                ShowContextMenu();
            }

            return IntPtr.Zero;
        }

        if (msg == WM_COMMAND)
        {
            int menuId = (int)(wParam & 0xFFFF);
            switch (menuId)
            {
                case MenuIdShow:
                    ShowRequested?.Invoke();
                    break;
                case MenuIdSettings:
                    SettingsRequested?.Invoke();
                    break;
                case MenuIdQuit:
                    QuitRequested?.Invoke();
                    break;
            }

            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenuW(hMenu, MF_STRING, (nuint)MenuIdShow, "Show");
            AppendMenuW(hMenu, MF_STRING, (nuint)MenuIdSettings, "Settings");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            AppendMenuW(hMenu, MF_STRING, (nuint)MenuIdQuit, "Quit Veil");

            GetCursorPos(out var pt);

            SetForegroundWindow(_hwnd);

            int cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_BOTTOMALIGN,
                pt.X, pt.Y, _hwnd, IntPtr.Zero);

            if (cmd > 0)
            {
                PostMessageW(_hwnd, WM_COMMAND, (IntPtr)cmd, IntPtr.Zero);
            }
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Shell_NotifyIconW(NIM_DELETE, ref _nid);

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            WndProc? wndProcDelegate = _wndProcDelegate;
            DestroyWindow(_hwnd);
            GC.KeepAlive(wndProcDelegate);
            _hwnd = IntPtr.Zero;
        }

        _wndProcDelegate = null;

        AppLogger.Info("Tray icon disposed.");
    }
}
