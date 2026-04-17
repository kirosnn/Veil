using System.Windows.Automation;
using System.Runtime.InteropServices;
using Veil.Diagnostics;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal enum TextInsertionResultKind
{
    Inserted,
    CopiedToClipboard
}

internal sealed record TextInsertionResult(TextInsertionResultKind Kind, string Message);
internal sealed record TextInsertionTarget(IntPtr WindowHandle, IntPtr FocusHandle, IntPtr CaretHandle, uint ThreadId, AutomationElement? EditableElement);

internal sealed class FocusedTextInsertionService
{
    private const int ForegroundActivationDelayMs = 24;
    private const int ForegroundActivationPollIntervalMs = 10;
    private const int ForegroundActivationMaxWaitMs = 300;
    private const int ClipboardPropagationDelayMs = 20;

    internal TextInsertionTarget CaptureInsertionTarget(IntPtr targetWindow)
    {
        return ResolveInsertionTarget(targetWindow);
    }

    internal async Task<TextInsertionResult> InsertAsync(string text, IntPtr targetWindow, CancellationToken cancellationToken)
    {
        return await InsertAsync(text, ResolveInsertionTarget(targetWindow), cancellationToken);
    }

    internal async Task<TextInsertionResult> InsertAsync(string text, TextInsertionTarget insertionTarget, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TextInsertionResult(TextInsertionResultKind.CopiedToClipboard, "Nothing to insert.");
        }

        if (insertionTarget.WindowHandle == IntPtr.Zero)
        {
            return await FallbackToClipboardAsync(
                text,
                "No insertion target was available. Text was copied to the clipboard.");
        }

        IntPtr currentForeground = GetForegroundWindow();
        bool needsForegroundActivation = currentForeground != insertionTarget.WindowHandle;
        AppLogger.Info(
            $"Dictation insert: foreground=0x{currentForeground.ToInt64():X} target=0x{insertionTarget.WindowHandle.ToInt64():X} needsActivation={needsForegroundActivation}.");

        if (IsIconic(insertionTarget.WindowHandle))
        {
            ShowWindowNative(insertionTarget.WindowHandle, SW_RESTORE);
            needsForegroundActivation = true;
        }

        if (needsForegroundActivation)
        {
            SetForegroundWindow(insertionTarget.WindowHandle);
            await WaitForForegroundWindowAsync(insertionTarget.WindowHandle, cancellationToken);

            if (TryRestoreFocus(insertionTarget))
            {
                await Task.Delay(ForegroundActivationDelayMs, cancellationToken);
            }
        }

        TextInsertionTarget effectiveInsertionTarget = RefreshInsertionTarget(insertionTarget);

        AppLogger.Info(
            $"Dictation target window=0x{effectiveInsertionTarget.WindowHandle.ToInt64():X} focus=0x{effectiveInsertionTarget.FocusHandle.ToInt64():X} caret=0x{effectiveInsertionTarget.CaretHandle.ToInt64():X} class={GetTargetClassName(effectiveInsertionTarget) ?? "unknown"} automation={(effectiveInsertionTarget.EditableElement is not null)}.");

        if (TryInsertWithControlMessage(text, effectiveInsertionTarget, out string? controlMessageMethod))
        {
            AppLogger.Info($"Dictation inserted using {controlMessageMethod}.");
            return new TextInsertionResult(
                TextInsertionResultKind.Inserted,
                text.Length > 80 ? text[..80] + "..." : text);
        }

        if (await TryInsertWithPasteAsync(text, effectiveInsertionTarget, cancellationToken))
        {
            AppLogger.Info("Dictation inserted using clipboard paste.");
            return new TextInsertionResult(
                TextInsertionResultKind.Inserted,
                text.Length > 80 ? text[..80] + "..." : text);
        }

        if (await TryInsertWithSendInputAsync(text))
        {
            AppLogger.Info("Dictation inserted using SendInput text entry.");
            return new TextInsertionResult(
                TextInsertionResultKind.Inserted,
                text.Length > 80 ? text[..80] + "..." : text);
        }

        AppLogger.Info("Dictation fell back to clipboard copy only.");
        return await FallbackToClipboardAsync(
            text,
            "Text was copied to the clipboard because direct insertion failed.");
    }

    private static TextInsertionTarget RefreshInsertionTarget(TextInsertionTarget insertionTarget)
    {
        if (insertionTarget.WindowHandle == IntPtr.Zero)
        {
            return insertionTarget;
        }

        TextInsertionTarget refreshedTarget = ResolveInsertionTarget(insertionTarget.WindowHandle);
        return refreshedTarget.WindowHandle == IntPtr.Zero
            ? insertionTarget
            : refreshedTarget with
            {
                EditableElement = refreshedTarget.EditableElement ?? insertionTarget.EditableElement
            };
    }

    internal Task<TextInsertionResult> FallbackToClipboardAsync(string text, string message)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(new TextInsertionResult(TextInsertionResultKind.CopiedToClipboard, "Nothing to copy."));
        }

        SetClipboardText(text);
        return Task.FromResult(new TextInsertionResult(TextInsertionResultKind.CopiedToClipboard, message));
    }

    private static TextInsertionTarget ResolveInsertionTarget(IntPtr targetWindow)
    {
        IntPtr windowHandle = ResolveInsertionWindow(targetWindow);
        if (windowHandle == IntPtr.Zero)
        {
            return new TextInsertionTarget(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, null);
        }

        uint threadId = GetWindowThreadProcessId(windowHandle, out _);
        if (threadId == 0)
        {
            return new TextInsertionTarget(windowHandle, IntPtr.Zero, IntPtr.Zero, 0, FindEditableElement());
        }

        GuiThreadInfo threadInfo = GuiThreadInfo.Create();
        if (!GetGUIThreadInfo(threadId, ref threadInfo))
        {
            return new TextInsertionTarget(windowHandle, IntPtr.Zero, IntPtr.Zero, threadId, FindEditableElement());
        }

        IntPtr focusHandle = NormalizeTargetHandle(threadInfo.hwndFocus, windowHandle);
        IntPtr caretHandle = NormalizeTargetHandle(threadInfo.hwndCaret, windowHandle);
        return new TextInsertionTarget(windowHandle, focusHandle, caretHandle, threadId, FindEditableElement());
    }

    private static IntPtr ResolveInsertionWindow(IntPtr targetWindow)
    {
        IntPtr normalizedTargetWindow = NormalizeWindowHandle(targetWindow);
        if (normalizedTargetWindow != IntPtr.Zero && IsWindow(normalizedTargetWindow))
        {
            return normalizedTargetWindow;
        }

        IntPtr normalizedForegroundWindow = NormalizeWindowHandle(GetForegroundWindow());
        if (normalizedForegroundWindow != IntPtr.Zero && IsWindow(normalizedForegroundWindow))
        {
            return normalizedForegroundWindow;
        }

        return IntPtr.Zero;
    }

    private static IntPtr NormalizeWindowHandle(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr rootOwner = GetAncestor(windowHandle, GA_ROOTOWNER);
        return rootOwner != IntPtr.Zero ? rootOwner : windowHandle;
    }

    private static IntPtr NormalizeTargetHandle(IntPtr handle, IntPtr rootWindowHandle)
    {
        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            return IntPtr.Zero;
        }

        IntPtr targetRootWindow = GetAncestor(handle, GA_ROOT);
        if (targetRootWindow == IntPtr.Zero)
        {
            targetRootWindow = NormalizeWindowHandle(handle);
        }

        return targetRootWindow == rootWindowHandle ? handle : IntPtr.Zero;
    }

    private static bool TryRestoreFocus(TextInsertionTarget insertionTarget)
    {
        if (TryRestoreAutomationFocus(insertionTarget.EditableElement))
        {
            return true;
        }

        IntPtr focusHandle = insertionTarget.FocusHandle != IntPtr.Zero && IsWindow(insertionTarget.FocusHandle)
            ? insertionTarget.FocusHandle
            : insertionTarget.CaretHandle != IntPtr.Zero && IsWindow(insertionTarget.CaretHandle)
                ? insertionTarget.CaretHandle
                : IntPtr.Zero;

        if (focusHandle == IntPtr.Zero)
        {
            return false;
        }

        uint targetThreadId = insertionTarget.ThreadId != 0
            ? insertionTarget.ThreadId
            : GetWindowThreadProcessId(insertionTarget.WindowHandle, out _);
        uint currentThreadId = GetCurrentThreadId();
        bool attached = false;

        try
        {
            if (targetThreadId != 0 && targetThreadId != currentThreadId)
            {
                attached = AttachThreadInput(currentThreadId, targetThreadId, true);
            }

            SetFocus(focusHandle);
            return GetFocus() == focusHandle;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThreadId, targetThreadId, false);
            }
        }
    }

    private static bool TryInsertWithControlMessage(string text, TextInsertionTarget insertionTarget, out string? method)
    {
        method = null;

        IntPtr targetHandle = ResolveBestTargetHandle(insertionTarget);
        if (targetHandle == IntPtr.Zero)
        {
            return false;
        }

        string? className = GetWindowClassName(targetHandle);
        if (!IsStandardEditableClass(className))
        {
            return false;
        }

        try
        {
            SendMessageStringW(targetHandle, EM_REPLACESEL, new IntPtr(1), text);
            method = $"EM_REPLACESEL ({className})";
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Dictation control-message insertion failed.", ex);
            return false;
        }
    }

    // No clipboard save/restore: avoids the race where RestoreClipboardTextAsync fires 120ms
    // after FallbackToClipboard has already set the transcribed text, overwriting it.
    private static async Task<bool> TryInsertWithPasteAsync(string text, TextInsertionTarget insertionTarget, CancellationToken cancellationToken)
    {
        try
        {
            IntPtr targetHandle = ResolveBestTargetHandle(insertionTarget);
            string? className = targetHandle != IntPtr.Zero ? GetWindowClassName(targetHandle) : null;

            if (targetHandle != IntPtr.Zero && IsStandardEditableClass(className))
            {
                if (!SetClipboardText(text))
                {
                    AppLogger.Info("Dictation paste: clipboard set failed for WM_PASTE path.");
                    return false;
                }

                await Task.Delay(ClipboardPropagationDelayMs, cancellationToken);
                SendMessageW(targetHandle, WM_PASTE, IntPtr.Zero, IntPtr.Zero);
                return true;
            }

            // For modern apps (WinUI, WPF, Electron, browsers, terminals): Ctrl+V.
            IntPtr foregroundForPaste = GetForegroundWindow();
            AppLogger.Info(
                $"Dictation paste: foreground=0x{foregroundForPaste.ToInt64():X} target=0x{insertionTarget.WindowHandle.ToInt64():X}.");

            if (foregroundForPaste != insertionTarget.WindowHandle)
            {
                SetForegroundWindow(insertionTarget.WindowHandle);
                await Task.Delay(ForegroundActivationDelayMs, cancellationToken);
                IntPtr foregroundAfter = GetForegroundWindow();
                AppLogger.Info(
                    $"Dictation paste: after SetForegroundWindow, foreground=0x{foregroundAfter.ToInt64():X}.");
                // Do not abort if SetForegroundWindow failed — still attempt Ctrl+V;
                // worst case it lands in the wrong window, but returning false here
                // causes silent fallback to clipboard-only.
            }

            if (!SetClipboardText(text))
            {
                AppLogger.Info("Dictation paste: clipboard set failed for Ctrl+V path.");
                return false;
            }

            await Task.Delay(ClipboardPropagationDelayMs, cancellationToken);
            bool ctrlVSent = await SendCtrlVAsync();
            AppLogger.Info($"Dictation paste: SendCtrlV result={ctrlVSent}.");
            return ctrlVSent;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Dictation paste insertion failed.", ex);
            return false;
        }
    }

    private static AutomationElement? FindEditableElement()
    {
        try
        {
            AutomationElement? current = AutomationElement.FocusedElement;
            while (current is not null)
            {
                if (IsEditableElement(current))
                {
                    return current;
                }

                current = TreeWalker.ControlViewWalker.GetParent(current);
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool IsEditableElement(AutomationElement element)
    {
        try
        {
            ControlType controlType = element.Current.ControlType;
            if (controlType == ControlType.Edit || controlType == ControlType.Document)
            {
                return true;
            }

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? valuePatternObject))
            {
                var valuePattern = (ValuePattern)valuePatternObject;
                return !valuePattern.Current.IsReadOnly;
            }

            return element.TryGetCurrentPattern(TextPattern.Pattern, out _);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRestoreAutomationFocus(AutomationElement? editableElement)
    {
        if (editableElement is null)
        {
            return false;
        }

        try
        {
            editableElement.SetFocus();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Task<bool> TryInsertWithSendInputAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult(false);
        }

        string normalizedText = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var inputs = new Input[normalizedText.Length * 2];
        int inputIndex = 0;

        foreach (char character in normalizedText)
        {
            char inputCharacter = character == '\n' ? '\r' : character;
            inputs[inputIndex++] = CreateKeyboardInput(inputCharacter, KEYEVENTF_UNICODE);
            inputs[inputIndex++] = CreateKeyboardInput(inputCharacter, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
        }

        return Task.Run(() =>
        {
            uint sentInputCount = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            AppLogger.Info($"Dictation SendInput unicode: sent={sentInputCount} expected={inputs.Length} foreground=0x{GetForegroundWindow().ToInt64():X}.");
            return sentInputCount == (uint)inputs.Length;
        });
    }

    private static Input CreateKeyboardInput(char character, uint flags)
    {
        return new Input
        {
            type = INPUT_KEYBOARD,
            Anonymous = new InputUnion
            {
                ki = new KeybdInput
                {
                    wVk = 0,
                    wScan = character,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private static async Task WaitForForegroundWindowAsync(IntPtr windowHandle, CancellationToken cancellationToken)
    {
        int elapsed = 0;
        while (elapsed < ForegroundActivationMaxWaitMs && !cancellationToken.IsCancellationRequested)
        {
            if (GetForegroundWindow() == windowHandle)
            {
                return;
            }

            await Task.Delay(ForegroundActivationPollIntervalMs, cancellationToken);
            elapsed += ForegroundActivationPollIntervalMs;
        }
    }

    // SendInput must run off the UI thread: the WH_KEYBOARD_LL hook is installed on the UI
    // thread, so calling SendInput from the UI thread deadlocks — the hook message can never
    // be delivered while the thread is blocked inside SendInput.
    private static Task<bool> SendCtrlVAsync()
    {
        return Task.Run(() =>
        {
            var inputs = new Input[4];
            inputs[0] = CreateVirtualKeyInput((ushort)VK_CONTROL, 0);
            inputs[1] = CreateVirtualKeyInput((ushort)VK_V, 0);
            inputs[2] = CreateVirtualKeyInput((ushort)VK_V, KEYEVENTF_KEYUP);
            inputs[3] = CreateVirtualKeyInput((ushort)VK_CONTROL, KEYEVENTF_KEYUP);
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == (uint)inputs.Length;
        });
    }

    private static Input CreateVirtualKeyInput(ushort vk, uint flags)
    {
        return new Input
        {
            type = INPUT_KEYBOARD,
            Anonymous = new InputUnion
            {
                ki = new KeybdInput
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private static bool SetClipboardText(string text)
    {
        bool opened = false;
        for (int attempt = 0; attempt < 5 && !opened; attempt++)
        {
            opened = OpenClipboard(IntPtr.Zero);
            if (!opened && attempt < 4)
            {
                System.Threading.Thread.Sleep(10);
            }
        }

        if (!opened)
        {
            AppLogger.Info("Dictation: OpenClipboard failed after retries.");
            return false;
        }

        try
        {
            EmptyClipboard();
            int charCount = text.Length + 1;
            IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (nuint)(charCount * sizeof(char)));
            if (hGlobal == IntPtr.Zero)
            {
                return false;
            }

            IntPtr ptr = GlobalLock(hGlobal);
            if (ptr == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }

            Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
            Marshal.WriteInt16(ptr + text.Length * sizeof(char), 0);
            GlobalUnlock(hGlobal);

            if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }

            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static IntPtr ResolveBestTargetHandle(TextInsertionTarget insertionTarget)
    {
        if (insertionTarget.FocusHandle != IntPtr.Zero && IsWindow(insertionTarget.FocusHandle))
        {
            return insertionTarget.FocusHandle;
        }

        if (insertionTarget.CaretHandle != IntPtr.Zero && IsWindow(insertionTarget.CaretHandle))
        {
            return insertionTarget.CaretHandle;
        }

        return insertionTarget.WindowHandle;
    }

    private static string? GetTargetClassName(TextInsertionTarget insertionTarget)
    {
        IntPtr targetHandle = ResolveBestTargetHandle(insertionTarget);
        return targetHandle == IntPtr.Zero ? null : GetWindowClassName(targetHandle);
    }

    private static string? GetWindowClassName(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        char[] buffer = new char[256];
        int length = GetClassNameW(handle, buffer, buffer.Length);
        if (length <= 0)
        {
            return null;
        }

        return new string(buffer, 0, length);
    }

    private static bool IsStandardEditableClass(string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        return className.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
               className.Contains("RichEdit", StringComparison.OrdinalIgnoreCase) ||
               className.Contains("RichEditD2D", StringComparison.OrdinalIgnoreCase) ||
               className.StartsWith("WindowsForms10.EDIT", StringComparison.OrdinalIgnoreCase);
    }
}
