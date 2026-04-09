using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed class DictationHotkeyService : IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _hookHandle;
    private bool _capsPressed;
    private bool _comboActive;
    private bool _capsUsedAsModifier;
    private bool _disposed;

    internal DictationHotkeyService(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    internal Func<bool>? CanStartCapture { get; set; }

    internal event Action? CaptureStarted;
    internal event Action? CaptureStopped;

    internal void Initialize()
    {
        if (_disposed || _hookHandle != IntPtr.Zero)
        {
            return;
        }

        _hookProc = HookCallback;
        _hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, _hookProc, GetModuleHandleW(null), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < HC_ACTION || lParam == IntPtr.Zero)
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        if ((data.flags & LLKHF_INJECTED) != 0)
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        uint message = unchecked((uint)wParam.ToInt64());
        bool isKeyDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool isKeyUp = message is WM_KEYUP or WM_SYSKEYUP;

        switch (data.vkCode)
        {
            case VK_CAPITAL:
                if (isKeyDown)
                {
                    _capsPressed = true;
                    _capsUsedAsModifier = false;
                    return (IntPtr)1;
                }

                if (isKeyUp)
                {
                    _capsPressed = false;

                    if (_comboActive)
                    {
                        _comboActive = false;
                        Enqueue(() => CaptureStopped?.Invoke());
                        return (IntPtr)1;
                    }

                    if (!_capsUsedAsModifier)
                    {
                        SimulateCapsLockToggle();
                    }

                    return (IntPtr)1;
                }

                break;

            case VK_SPACE:
                if (_capsPressed || _comboActive)
                {
                    _capsUsedAsModifier = true;

                    if (isKeyDown)
                    {
                        if (!_comboActive)
                        {
                            _comboActive = true;
                            if (CanStartCapture?.Invoke() == true)
                            {
                                Enqueue(() => CaptureStarted?.Invoke());
                            }
                        }

                        return (IntPtr)1;
                    }

                    if (isKeyUp)
                    {
                        if (_comboActive)
                        {
                            _comboActive = false;
                            Enqueue(() => CaptureStopped?.Invoke());
                        }

                        return (IntPtr)1;
                    }
                }

                break;

            default:
                if (_capsPressed)
                {
                    _capsUsedAsModifier = true;
                }
                break;
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void SimulateCapsLockToggle()
    {
        KeybdEvent((byte)VK_CAPITAL, 0, 0, 0);
        KeybdEvent((byte)VK_CAPITAL, 0, KEYEVENTF_KEYUP, 0);
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
