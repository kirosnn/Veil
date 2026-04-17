using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed class DictationHotkeyService : IDisposable
{
    private const int LearningWindowMs = 30_000;

    // Fn key candidates: Apple Fn on Windows (0xFF), F24/some laptop Fn (0x87), extended key with scan=0x00
    private static readonly uint[] FnCandidateVkCodes = [0xFF, 0x87];

    private readonly DispatcherQueue _dispatcherQueue;
    private LowLevelKeyboardProc? _hookProc;
    private IntPtr _hookHandle;
    private bool _isHeld;
    private bool _captureActive;
    private bool _disposed;
    private uint _triggerVkCode;
    private System.Threading.Timer? _learningTimer;

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

        // Default to Right Ctrl immediately so it works from the first keystroke.
        // Within the learning window, if a Fn key is detected, it replaces Right Ctrl.
        _triggerVkCode = VK_RCONTROL;
        _learningTimer = new System.Threading.Timer(_ => StopLearning(), null, LearningWindowMs, Timeout.Infinite);

        _hookProc = HookCallback;
        _hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, _hookProc, GetModuleHandleW(null), 0);
    }

    private void StopLearning()
    {
        _learningTimer?.Dispose();
        _learningTimer = null;
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

        // During the learning window, watch for a Fn key and promote it to trigger.
        if (_learningTimer != null && IsFnCandidate(data))
        {
            _triggerVkCode = data.vkCode != 0 ? data.vkCode : 0xFF;
            StopLearning();
        }

        if (data.vkCode != _triggerVkCode)
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        uint message = unchecked((uint)wParam.ToInt64());
        bool isKeyDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool isKeyUp = message is WM_KEYUP or WM_SYSKEYUP;

        if (isKeyDown && !_isHeld)
        {
            _isHeld = true;
            if (!_captureActive && CanStartCapture?.Invoke() == true)
            {
                _captureActive = true;
                Enqueue(() => CaptureStarted?.Invoke());
            }
            return (IntPtr)1;
        }

        if (isKeyUp && _isHeld)
        {
            _isHeld = false;
            if (_captureActive)
            {
                _captureActive = false;
                Enqueue(() => CaptureStopped?.Invoke());
            }
            return (IntPtr)1;
        }

        if (_isHeld)
        {
            return (IntPtr)1;
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static bool IsFnCandidate(KbdLlHookStruct data)
    {
        if (Array.IndexOf(FnCandidateVkCodes, data.vkCode) >= 0)
        {
            return true;
        }

        // scanCode=0 with no virtual key is a signature for Fn on some keyboards
        return data.vkCode == 0 && data.scanCode == 0;
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

        _learningTimer?.Dispose();
        _learningTimer = null;

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        _hookProc = null;
    }
}
