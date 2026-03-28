using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed class AltTabHookService : IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _hookHandle;
    private bool _altPressed;
    private bool _shiftPressed;
    private bool _switcherActive;
    private bool _disposed;

    internal event Action<bool>? SwitchStarted;
    internal event Action<bool>? SwitchStepped;
    internal event Action? SwitchCommitted;
    internal event Action? SwitchCanceled;

    internal AltTabHookService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    internal void Initialize()
    {
        if (_disposed || _hookHandle != IntPtr.Zero)
        {
            return;
        }

        _hookProc = HookCallback;
        _hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, _hookProc, GetModuleHandleW(null), 0);
    }

    internal void ResetSession()
    {
        _switcherActive = false;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < HC_ACTION || lParam == IntPtr.Zero)
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        uint message = unchecked((uint)wParam.ToInt64());
        bool isKeyDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool isKeyUp = message is WM_KEYUP or WM_SYSKEYUP;

        switch (data.vkCode)
        {
            case VK_SHIFT:
            case VK_LSHIFT:
            case VK_RSHIFT:
                if (isKeyDown)
                {
                    _shiftPressed = true;
                }
                else if (isKeyUp)
                {
                    _shiftPressed = false;
                }
                break;

            case VK_MENU:
            case VK_LMENU:
            case VK_RMENU:
                if (isKeyDown)
                {
                    _altPressed = true;
                }
                else if (isKeyUp)
                {
                    bool wasSwitcherActive = _switcherActive;
                    _altPressed = false;
                    _shiftPressed = false;
                    _switcherActive = false;

                    if (wasSwitcherActive)
                    {
                        Enqueue(() => SwitchCommitted?.Invoke());
                    }
                }

                if (_switcherActive)
                {
                    return (IntPtr)1;
                }
                break;

            case VK_TAB:
                if (isKeyDown && _altPressed)
                {
                    bool moveForward = !_shiftPressed;
                    if (_switcherActive)
                    {
                        Enqueue(() => SwitchStepped?.Invoke(moveForward));
                    }
                    else
                    {
                        _switcherActive = true;
                        Enqueue(() => SwitchStarted?.Invoke(moveForward));
                    }

                    return (IntPtr)1;
                }

                if (_switcherActive)
                {
                    return (IntPtr)1;
                }
                break;

            case VK_ESCAPE:
                if (isKeyDown && _switcherActive)
                {
                    _switcherActive = false;
                    Enqueue(() => SwitchCanceled?.Invoke());
                    return (IntPtr)1;
                }
                break;
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void Enqueue(Action action)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed)
            {
                action();
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        _hookProc = null;
    }
}
