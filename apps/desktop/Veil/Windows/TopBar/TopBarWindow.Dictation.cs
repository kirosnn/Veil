using Veil.Diagnostics;
using Veil.Services;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class TopBarWindow
{
    private DictationHotkeyService? _dictationHotkeyService;
    private DictationOverlayWindow? _dictationOverlayWindow;
    private MicrophoneRecordingService? _microphoneRecordingService;
    private readonly LocalSpeechTranscriptionService _localSpeechTranscriptionService = new();
    private readonly FocusedTextInsertionService _focusedTextInsertionService = new();
    private bool _isDictationBusy;
    private bool _isDictationCancelling;
    private IntPtr _dictationTargetWindow;
    private TextInsertionTarget? _dictationInsertionTarget;
    private string? _dictationRecordingPath;
    private CancellationTokenSource? _dictationOperationCts;
    private CancellationToken _dictationOperationToken = CancellationToken.None;
    private Task<string?>? _dictationStopTask;

    private void InitializeDictationHotkey()
    {
        if (_dictationHotkeyService is not null)
        {
            return;
        }

        _dictationHotkeyService = new DictationHotkeyService(DispatcherQueue)
        {
            CanStartCapture = CanStartDictationCapture
        };
        _dictationHotkeyService.CaptureStarted += OnDictationCaptureStarted;
        _dictationHotkeyService.CaptureStopped += OnDictationCaptureStopped;
        _dictationHotkeyService.Initialize();
        _microphoneRecordingService ??= new MicrophoneRecordingService();
        _microphoneRecordingService.SpectrumChanged += OnDictationSpectrumChanged;
        EnsureDictationOverlayWindowCreated();
    }

    private void DisposeDictationHotkey()
    {
        if (_dictationHotkeyService is not null)
        {
            _dictationHotkeyService.CaptureStarted -= OnDictationCaptureStarted;
            _dictationHotkeyService.CaptureStopped -= OnDictationCaptureStopped;
            _dictationHotkeyService.Dispose();
            _dictationHotkeyService = null;
        }

        if (_microphoneRecordingService is not null)
        {
            _microphoneRecordingService.SpectrumChanged -= OnDictationSpectrumChanged;
            _microphoneRecordingService.Dispose();
        }
        _microphoneRecordingService = null;
        _dictationOverlayWindow?.Close();
        _dictationOverlayWindow = null;
        _dictationOperationCts?.Cancel();
        _dictationOperationCts?.Dispose();
        _dictationOperationCts = null;
        _dictationOperationToken = CancellationToken.None;
        _dictationStopTask = null;
        _dictationRecordingPath = null;
        _dictationInsertionTarget = null;
        _isDictationBusy = false;
        _isDictationCancelling = false;
    }

    private bool CanStartDictationCapture()
    {
        return !_isGameMinimalMode && !_isDictationBusy;
    }

    private async void OnDictationCaptureStarted()
    {
        if (!CanStartDictationCapture())
        {
            return;
        }

        if (!_localSpeechTranscriptionService.CanTranscribe(_settings))
        {
            _dictationOverlayWindow?.ShowStatus(_screen, "Setup Required", "Download a speech model in Settings to use Dictation.");
            await Task.Delay(2500);
            _dictationOverlayWindow?.HideOverlay();
            return;
        }

        _isDictationBusy = true;
        _isDictationCancelling = false;
        _dictationOperationCts?.Cancel();
        _dictationOperationCts?.Dispose();
        _dictationOperationCts = new CancellationTokenSource();
        _dictationOperationToken = _dictationOperationCts.Token;
        _dictationStopTask = null;
        _dictationTargetWindow = GetForegroundWindow();
        _dictationInsertionTarget = _focusedTextInsertionService.CaptureInsertionTarget(_dictationTargetWindow);
        AppLogger.Info(
            $"Dictation capture started targetWindow=0x{_dictationTargetWindow.ToInt64():X} capturedWindow=0x{(_dictationInsertionTarget?.WindowHandle ?? IntPtr.Zero).ToInt64():X}.");
        _dictationOverlayWindow?.ShowStatus(_screen, "Listening", "Release Right Ctrl to transcribe.");

        try
        {
            _microphoneRecordingService ??= new MicrophoneRecordingService();
            await _microphoneRecordingService.StartAsync(_dictationOperationToken);
        }
        catch (OperationCanceledException)
        {
            _dictationOverlayWindow?.HideOverlay();
            _isDictationBusy = false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to start dictation recording.", ex);
            _dictationOverlayWindow?.ShowStatus(_screen, "Microphone Error", ex.Message);
            await Task.Delay(1850);
            _dictationOverlayWindow?.HideOverlay();
            _isDictationBusy = false;
        }
    }

    private async void OnDictationCaptureStopped()
    {
        if (!_isDictationBusy || _microphoneRecordingService is null)
        {
            return;
        }

        string? recordingPath = null;
        string? transcribedText = null;
        CancellationToken operationToken = _dictationOperationToken;

        try
        {
            recordingPath = await StopDictationRecordingAsync();
            _dictationRecordingPath = recordingPath;
            if (_isDictationCancelling || string.IsNullOrWhiteSpace(recordingPath))
            {
                _dictationOverlayWindow?.HideOverlay();
                _isDictationBusy = false;
                return;
            }

            _dictationOverlayWindow?.ShowStatus(_screen, "Transcribing", "Processing your speech locally.");
            transcribedText = await _localSpeechTranscriptionService.TranscribeAsync(
                _settings,
                recordingPath,
                operationToken);

            if (!_isDictationCancelling && !string.IsNullOrWhiteSpace(transcribedText))
            {
                TextInsertionResult insertionResult = await _focusedTextInsertionService.InsertAsync(
                    transcribedText,
                    _dictationInsertionTarget ?? _focusedTextInsertionService.CaptureInsertionTarget(_dictationTargetWindow),
                    operationToken);
                _dictationOverlayWindow?.ShowStatus(
                    _screen,
                    insertionResult.Kind == TextInsertionResultKind.Inserted ? "Inserted" : "Copied",
                    insertionResult.Message);
                AppLogger.Info($"Dictation insertion result={insertionResult.Kind}.");
                await Task.Delay(1700);
            }
            else
            {
                _dictationOverlayWindow?.HideOverlay();
            }
        }
        catch (OperationCanceledException)
        {
            _dictationOverlayWindow?.HideOverlay();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Dictation transcription failed.", ex);

            if (!_isDictationCancelling && !string.IsNullOrWhiteSpace(transcribedText))
            {
                TextInsertionResult clipboardFallbackResult = await _focusedTextInsertionService.FallbackToClipboardAsync(
                    transcribedText,
                    "Text was copied to the clipboard because insertion failed.");
                _dictationOverlayWindow?.ShowStatus(_screen, "Copied", clipboardFallbackResult.Message);
                await Task.Delay(1700);
            }
            else
            {
                _dictationOverlayWindow?.ShowStatus(_screen, "Dictation Error", ex.Message);
                await Task.Delay(1850);
            }
        }
        finally
        {
            _microphoneRecordingService.DeleteRecording(recordingPath ?? _dictationRecordingPath);
            _dictationRecordingPath = null;
            _dictationInsertionTarget = null;
            _dictationOverlayWindow?.HideOverlay();
            _isDictationBusy = false;
            _isDictationCancelling = false;
            _dictationOperationToken = CancellationToken.None;
            _dictationOperationCts?.Dispose();
            _dictationOperationCts = null;
            _dictationStopTask = null;
        }
    }

    private void EnsureDictationOverlayWindowCreated()
    {
        if (_dictationOverlayWindow is not null)
        {
            return;
        }

        _dictationOverlayWindow = new DictationOverlayWindow();
        _dictationOverlayWindow.DismissRequested += OnDictationOverlayDismissRequested;
        _dictationOverlayWindow.Activate();
        _dictationOverlayWindow.HideOverlay();
    }

    private void OnDictationSpectrumChanged(IReadOnlyList<float> levels)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _dictationOverlayWindow?.SetSpectrumLevels(levels);
        });
    }

    private void OnDictationOverlayDismissRequested()
    {
        _ = CancelActiveDictationAsync();
    }

    private async Task CancelActiveDictationAsync()
    {
        if (!_isDictationBusy)
        {
            _dictationOverlayWindow?.HideOverlay();
            return;
        }

        _isDictationCancelling = true;
        _dictationOperationCts?.Cancel();

        string? recordingPath = null;
        if (_microphoneRecordingService?.IsRecording == true)
        {
            try
            {
                recordingPath = await StopDictationRecordingAsync();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to cancel active dictation recording.", ex);
            }
        }

        _microphoneRecordingService?.DeleteRecording(recordingPath ?? _dictationRecordingPath);
        _dictationRecordingPath = null;
        _dictationInsertionTarget = null;
        _dictationOverlayWindow?.HideOverlay();
        _isDictationBusy = false;
    }

    private Task<string?> StopDictationRecordingAsync()
    {
        if (_dictationStopTask is not null)
        {
            return _dictationStopTask;
        }

        if (_microphoneRecordingService is null)
        {
            return Task.FromResult<string?>(null);
        }

        _dictationStopTask = _microphoneRecordingService.StopAsync();
        return _dictationStopTask;
    }
}
