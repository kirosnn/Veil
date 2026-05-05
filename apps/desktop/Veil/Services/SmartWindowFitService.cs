using Veil.Configuration;
using Veil.Diagnostics;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed class SmartWindowFitService : IDisposable
{
    private const int MaxPendingFits = 256;
    private const int RestoreSettleDelayMilliseconds = 120;
    private static readonly TimeSpan TopBarMonitorCacheDuration = TimeSpan.FromMilliseconds(1500);
    private readonly AppSettings _appSettings;
    private readonly NativeWindowApi _nativeApi;
    private readonly object _gate = new();
    private readonly object _topBarMonitorCacheGate = new();
    private readonly Dictionary<IntPtr, PendingFit> _pendingFits = [];
    private readonly HashSet<IntPtr> _processedWindows = [];
    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly WinEventProc _winEventProc;
    private WindowFitSettings _settings;
    private IntPtr _objectHook;
    private IReadOnlySet<string> _topBarMonitorIdCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private DateTime _topBarMonitorCacheExpiresUtc;
    private bool _disposed;

    internal SmartWindowFitService(AppSettings appSettings, NativeWindowApi? nativeApi = null)
    {
        _appSettings = appSettings;
        _nativeApi = nativeApi ?? new NativeWindowApi();
        _settings = WindowFitSettings.FromAppSettings(appSettings);
        _winEventProc = OnWinEvent;
    }

    internal void ApplySettings()
    {
        lock (_gate)
        {
            _settings = WindowFitSettings.FromAppSettings(_appSettings);
            InvalidateTopBarMonitorCache();
            if (_settings.Enabled)
            {
                StartCore();
            }
            else
            {
                StopCore();
            }
        }
    }

    internal void Start()
    {
        lock (_gate)
        {
            if (_settings.Enabled)
            {
                StartCore();
            }
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            StopCore();
        }
    }

    private void StartCore()
    {
        if (_disposed || _objectHook != IntPtr.Zero)
        {
            return;
        }

        uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        _objectHook = SetWinEventHook(
            EVENT_OBJECT_SHOW,
            EVENT_OBJECT_SHOW,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            flags);

        if (_objectHook == IntPtr.Zero)
        {
            AppLogger.Error("Smart Window Fit failed to install Win32 event hooks.");
            StopCore();
        }
    }

    private void StopCore()
    {
        if (_objectHook != IntPtr.Zero)
        {
            UnhookWinEvent(_objectHook);
            _objectHook = IntPtr.Zero;
        }

        foreach (PendingFit pending in _pendingFits.Values)
        {
            pending.Timer.Dispose();
        }

        _pendingFits.Clear();
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero)
        {
            return;
        }

        if (eventType != EVENT_OBJECT_SHOW)
        {
            return;
        }

        ScheduleFit(hwnd, GetForegroundWindow());
    }

    private void ScheduleFit(IntPtr hwnd, IntPtr foregroundWindow)
    {
        lock (_gate)
        {
            if (_disposed || !_settings.Enabled)
            {
                return;
            }

            if (_settings.RespectManualResize && _processedWindows.Contains(hwnd))
            {
                return;
            }

            if (_pendingFits.TryGetValue(hwnd, out PendingFit? existing))
            {
                existing.Timer.Change(_settings.DelayMilliseconds, Timeout.Infinite);
                return;
            }

            TrimPendingFitsIfNeeded();
            var pending = new PendingFit(hwnd, DateTime.UtcNow, foregroundWindow);
            pending.Timer = new Timer(OnFitTimer, hwnd, _settings.DelayMilliseconds, Timeout.Infinite);
            _pendingFits[hwnd] = pending;
        }
    }

    private void TrimPendingFitsIfNeeded()
    {
        if (_pendingFits.Count < MaxPendingFits)
        {
            return;
        }

        PendingFit oldest = _pendingFits.Values.MinBy(static pending => pending.FirstSeenUtc)!;
        oldest.Timer.Dispose();
        _pendingFits.Remove(oldest.Hwnd);
    }

    private void OnFitTimer(object? state)
    {
        if (state is not IntPtr hwnd)
        {
            return;
        }

        PendingFit? pending;
        WindowFitSettings settings;
        lock (_gate)
        {
            if (!_pendingFits.TryGetValue(hwnd, out pending) || !_settings.Enabled)
            {
                return;
            }

            settings = _settings;
        }

        try
        {
            FitResult result = FitWindow(hwnd, pending, settings);
            if (result == FitResult.RetryAfterRestore)
            {
                lock (_gate)
                {
                    if (_pendingFits.TryGetValue(hwnd, out PendingFit? current) && current == pending && _settings.Enabled)
                    {
                        current.Timer.Change(RestoreSettleDelayMilliseconds, Timeout.Infinite);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Smart Window Fit failed while fitting a window.", ex);
        }

        lock (_gate)
        {
            if (!_pendingFits.TryGetValue(hwnd, out PendingFit? current) || current != pending)
            {
                return;
            }

            current.Timer.Dispose();
            _pendingFits.Remove(hwnd);
        }
    }

    private FitResult FitWindow(IntPtr hwnd, PendingFit pending, WindowFitSettings settings)
    {
        if (!IsForegroundTarget(hwnd, pending))
        {
            return FitResult.Done;
        }

        if (!_nativeApi.TryCreateSnapshot(hwnd, out WindowFitNativeSnapshot snapshot))
        {
            return FitResult.Done;
        }

        if (!WindowFitCandidateEvaluator.ShouldFit(snapshot, settings, _ownProcessId))
        {
            return FitResult.Done;
        }

        if (!_nativeApi.TryGetMonitorLayout(hwnd, out MonitorWindowFitLayout monitorLayout))
        {
            return FitResult.Done;
        }

        if (!TryComputeTargetRect(monitorLayout, settings, out Rect targetRect))
        {
            return FitResult.Done;
        }

        Rect currentRect = snapshot.WindowRect;
        if (IsNearlyEqual(currentRect, targetRect))
        {
            MarkProcessed(hwnd, settings);
            return FitResult.Done;
        }

        if (snapshot.IsMaximized && !pending.RestoreAttempted)
        {
            pending.RestoreAttempted = true;
            _nativeApi.RestoreWindow(hwnd);
            return FitResult.RetryAfterRestore;
        }

        if (snapshot.IsMaximized)
        {
            _nativeApi.RestoreWindow(hwnd);
        }

        _nativeApi.SetWindowBounds(hwnd, targetRect);
        MarkProcessed(hwnd, settings);
        return FitResult.Done;
    }

    private static bool IsForegroundTarget(IntPtr hwnd, PendingFit pending)
    {
        IntPtr foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == hwnd)
        {
            return true;
        }

        return pending.ForegroundWindowAtShow == hwnd && foregroundWindow == IntPtr.Zero;
    }

    private bool TryComputeTargetRect(MonitorWindowFitLayout monitorLayout, WindowFitSettings settings, out Rect targetRect)
    {
        Rect usableArea = GetUsableArea(monitorLayout);
        int margin = settings.Margin;
        int usableLeft = usableArea.Left;
        int usableTop = usableArea.Top;
        int usableRight = usableArea.Right;
        int usableBottom = usableArea.Bottom;

        int targetWidth = Math.Max(0, usableRight - usableLeft - (margin * 2));
        int targetHeight = Math.Max(0, usableBottom - usableTop - (margin * 2));
        if (targetWidth < 360 || targetHeight < 240)
        {
            targetRect = default;
            return false;
        }

        int x = usableLeft + ((usableRight - usableLeft - targetWidth) / 2);
        int y = usableTop + ((usableBottom - usableTop - targetHeight) / 2);
        targetRect = new Rect
        {
            Left = x,
            Top = y,
            Right = x + targetWidth,
            Bottom = y + targetHeight
        };
        return true;
    }

    private Rect GetUsableArea(MonitorWindowFitLayout monitorLayout)
    {
        Rect monitor = monitorLayout.Monitor;
        Rect workArea = monitorLayout.WorkArea;

        int leftInset = Math.Max(0, workArea.Left - monitor.Left);
        int topInset = Math.Max(0, workArea.Top - monitor.Top);
        int rightInset = Math.Max(0, monitor.Right - workArea.Right);
        int bottomInset = Math.Max(0, monitor.Bottom - workArea.Bottom);

        bool topBarApplies = GetTopBarMonitorIds().Contains(monitorLayout.DeviceName);
        int topBarHeight = topBarApplies ? GetTopBarHeightInPhysicalPixels(monitorLayout) : 0;

        int topReserve = topBarApplies
            ? Math.Max(topInset, topBarHeight)
            : topInset;

        int bottomReserve = bottomInset;
        if (topBarApplies && topInset < topBarHeight)
        {
            bottomReserve = Math.Max(0, bottomInset - topBarHeight);
        }

        return new Rect
        {
            Left = monitor.Left + leftInset,
            Top = monitor.Top + topReserve,
            Right = monitor.Right - rightInset,
            Bottom = monitor.Bottom - bottomReserve
        };
    }

    private int GetTopBarHeightInPhysicalPixels(MonitorWindowFitLayout monitorLayout)
    {
        var screenBounds = new ScreenBounds(
            monitorLayout.Monitor.Left,
            monitorLayout.Monitor.Top,
            monitorLayout.Monitor.Right,
            monitorLayout.Monitor.Bottom);
        return Math.Max(1, WindowHelper.ViewPixelsToPhysical(screenBounds, _appSettings.TopBarHeight));
    }

    private IReadOnlySet<string> GetTopBarMonitorIds()
    {
        DateTime now = DateTime.UtcNow;
        lock (_topBarMonitorCacheGate)
        {
            if (now < _topBarMonitorCacheExpiresUtc)
            {
                return _topBarMonitorIdCache;
            }
        }

        List<MonitorInfo2> monitors = MonitorService.GetAllMonitors();
        IReadOnlyList<string> monitorIds = _appSettings.ResolveTopBarMonitorIds(monitors);
        HashSet<string> monitorIdSet = monitorIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_topBarMonitorCacheGate)
        {
            _topBarMonitorIdCache = monitorIdSet;
            _topBarMonitorCacheExpiresUtc = now + TopBarMonitorCacheDuration;
            return _topBarMonitorIdCache;
        }
    }

    private void InvalidateTopBarMonitorCache()
    {
        lock (_topBarMonitorCacheGate)
        {
            _topBarMonitorCacheExpiresUtc = DateTime.MinValue;
        }
    }

    private void MarkProcessed(IntPtr hwnd, WindowFitSettings settings)
    {
        if (!settings.RespectManualResize)
        {
            return;
        }

        lock (_gate)
        {
            _processedWindows.Add(hwnd);
            if (_processedWindows.Count > 512)
            {
                _processedWindows.Clear();
            }
        }
    }

    private static bool IsNearlyEqual(Rect a, Rect b)
        => Math.Abs(a.Left - b.Left) <= 2
            && Math.Abs(a.Top - b.Top) <= 2
            && Math.Abs(a.Right - b.Right) <= 2
            && Math.Abs(a.Bottom - b.Bottom) <= 2;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopCore();
        }
    }

    private sealed class PendingFit(IntPtr hwnd, DateTime firstSeenUtc, IntPtr foregroundWindowAtShow)
    {
        public IntPtr Hwnd { get; } = hwnd;
        public DateTime FirstSeenUtc { get; } = firstSeenUtc;
        public IntPtr ForegroundWindowAtShow { get; } = foregroundWindowAtShow;
        public bool RestoreAttempted { get; set; }
        public Timer Timer { get; set; } = null!;
    }

    private enum FitResult
    {
        Done,
        RetryAfterRestore
    }
}
