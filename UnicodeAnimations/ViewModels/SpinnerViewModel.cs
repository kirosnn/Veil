using System.ComponentModel;
using Microsoft.UI.Dispatching;
using UnicodeAnimations.Models;

namespace UnicodeAnimations.ViewModels;

/// <summary>
/// ViewModel for a single spinner card.
/// Drives its own <see cref="DispatcherQueueTimer"/> so each spinner
/// animates at its own interval without blocking the UI thread.
/// </summary>
public sealed class SpinnerViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly string[] _frames;
    private readonly DispatcherQueueTimer _timer;
    private int _frameIndex;
    private string _currentFrame;
    private bool _disposed;

    // ── Public properties ────────────────────────────────────────────────────

    public string Name { get; }

    /// <summary>e.g. "80 ms"</summary>
    public string IntervalLabel { get; }

    /// <summary>Number of animation frames, shown in the tooltip.</summary>
    public string FrameCountLabel { get; }

    /// <summary>The currently displayed Unicode frame string.</summary>
    public string CurrentFrame
    {
        get => _currentFrame;
        private set
        {
            if (_currentFrame == value) return;
            _currentFrame = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentFrame)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── Constructor ──────────────────────────────────────────────────────────

    public SpinnerViewModel(string name, Spinner spinner, DispatcherQueue queue)
    {
        Name          = name;
        _frames       = spinner.Frames;
        _currentFrame = _frames[0];
        IntervalLabel  = $"{spinner.Interval} ms";
        FrameCountLabel = $"{_frames.Length} frames";

        _timer = queue.CreateTimer();
        _timer.Interval    = TimeSpan.FromMilliseconds(spinner.Interval);
        _timer.IsRepeating = true;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    // ── Timer callback ───────────────────────────────────────────────────────

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        _frameIndex = (_frameIndex + 1) % _frames.Length;
        CurrentFrame = _frames[_frameIndex];
    }

    // ── Disposal ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _disposed = true;
    }
}
