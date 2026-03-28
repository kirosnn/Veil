using System.Runtime.InteropServices;
using Veil.Diagnostics;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed class FinderHotkeyService : IDisposable
{
    private const int FinderHotkeyId = 1;

    private IntPtr _hwnd;
    private WndProc? _wndProcDelegate;
    private bool _registered;
    private bool _disposed;

    public event Action? Triggered;

    public void Initialize()
    {
        if (_hwnd != IntPtr.Zero)
        {
            return;
        }

        _hwnd = CreateMessageWindow();
    }

    public void SetEnabled(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        Initialize();

        if (!enabled)
        {
            Unregister();
            return;
        }

        if (_registered)
        {
            return;
        }

        _registered = RegisterHotKey(_hwnd, FinderHotkeyId, MOD_CONTROL, VK_SPACE);
        if (!_registered)
        {
            AppLogger.Error("Failed to register Finder hotkey.", null);
        }
    }

    private IntPtr CreateMessageWindow()
    {
        var hInstance = GetModuleHandleW(null);
        _wndProcDelegate = WndProcHandler;
        const string className = "VeilFinderHotkeyWndClass";

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
            0, className, "Veil Finder Hotkey",
            0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    private IntPtr WndProcHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY && wParam == (IntPtr)FinderHotkeyId)
        {
            Triggered?.Invoke();
            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void Unregister()
    {
        if (!_registered || _hwnd == IntPtr.Zero)
        {
            return;
        }

        UnregisterHotKey(_hwnd, FinderHotkeyId);
        _registered = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
