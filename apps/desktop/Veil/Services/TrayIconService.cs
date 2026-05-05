using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
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
    private const int RetryLimit = 60;

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private NotifyIconData _nid;
    private WndProc? _wndProcDelegate;
    private DispatcherQueueTimer? _retryTimer;
    private uint _taskbarCreatedMessage;
    private int _retryCount;
    private bool _iconAdded;
    private bool _disposed;

    public event Action? ShowRequested;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;

    public void Initialize()
    {
        _hIcon = LoadAppIcon();
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        _hwnd = CreateMessageWindow();
        _iconAdded = AddTrayIcon();

        if (_iconAdded)
        {
            AppLogger.Info("Tray icon initialized.");
        }
        else
        {
            AppLogger.Error("Tray icon failed to initialize.");
            StartRetryTimer();
        }
    }

    private IntPtr LoadAppIcon()
    {
        var exePath = Environment.ProcessPath ?? "Veil.exe";
        var icon = ExtractIconW(IntPtr.Zero, exePath, 0);
        if (icon != IntPtr.Zero)
        {
            return icon;
        }

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Logo", "veil.ico");
        if (!File.Exists(iconPath))
        {
            AppLogger.Error($"Tray icon asset was not found at {iconPath}.");
            return IntPtr.Zero;
        }

        return LoadImageW(
            IntPtr.Zero,
            iconPath,
            IMAGE_ICON,
            0,
            0,
            LR_LOADFROMFILE | LR_DEFAULTSIZE);
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
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        return hwnd;
    }

    private bool AddTrayIcon()
    {
        if (_hwnd == IntPtr.Zero)
        {
            AppLogger.Error("Tray icon message window was not created.");
            return false;
        }

        if (_hIcon == IntPtr.Zero)
        {
            AppLogger.Error("Tray icon handle was not created.");
            return false;
        }

        _nid = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = (uint)WM_TRAYICON,
            hIcon = _hIcon,
            szTip = "Veil",
            szInfo = string.Empty,
            uVersion = NOTIFYICON_VERSION_4,
            szInfoTitle = string.Empty,
            guidItem = Guid.Empty
        };

        if (!Shell_NotifyIconW(NIM_ADD, ref _nid))
        {
            return false;
        }

        Shell_NotifyIconW(NIM_SETVERSION, ref _nid);
        return true;
    }

    private IntPtr WndProcHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
        {
            RecreateTrayIcon();
            return IntPtr.Zero;
        }

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

    private void RecreateTrayIcon()
    {
        if (_iconAdded)
        {
            Shell_NotifyIconW(NIM_DELETE, ref _nid);
            _iconAdded = false;
        }

        _iconAdded = AddTrayIcon();
        if (_iconAdded)
        {
            StopRetryTimer();
            AppLogger.Info("Tray icon restored.");
            return;
        }

        AppLogger.Error("Tray icon failed to restore.");
        StartRetryTimer();
    }

    private void StartRetryTimer()
    {
        if (_disposed || _iconAdded || _retryTimer is not null)
        {
            return;
        }

        DispatcherQueue? dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        if (dispatcherQueue is null)
        {
            AppLogger.Error("Tray icon retry timer could not be created.");
            return;
        }

        _retryCount = 0;
        _retryTimer = dispatcherQueue.CreateTimer();
        _retryTimer.Interval = TimeSpan.FromSeconds(2);
        _retryTimer.Tick += OnRetryTimerTick;
        _retryTimer.Start();
    }

    private void StopRetryTimer()
    {
        if (_retryTimer is null)
        {
            return;
        }

        _retryTimer.Stop();
        _retryTimer.Tick -= OnRetryTimerTick;
        _retryTimer = null;
        _retryCount = 0;
    }

    private void OnRetryTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _iconAdded)
        {
            StopRetryTimer();
            return;
        }

        _retryCount++;
        _iconAdded = AddTrayIcon();
        if (_iconAdded)
        {
            StopRetryTimer();
            AppLogger.Info("Tray icon initialized after retry.");
            return;
        }

        if (_retryCount >= RetryLimit)
        {
            StopRetryTimer();
            AppLogger.Error("Tray icon retry limit reached.");
        }
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
        StopRetryTimer();

        if (_iconAdded)
        {
            Shell_NotifyIconW(NIM_DELETE, ref _nid);
            _iconAdded = false;
        }

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
