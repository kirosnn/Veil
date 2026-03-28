using Veil.Diagnostics;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed class RunCatService : IDisposable
{
    private const int MaxSamples = 4;
    private static readonly TimeSpan ResourceSamplePeriod = TimeSpan.FromMilliseconds(900);
    private readonly List<double> _samples = new(MaxSamples);
    private readonly Timer _fetchTimer;
    private int _currentFrame;
    private bool _disposed;
    private double _smoothedPressure;
    private long _lastResourceSampleTick;

    public int FrameCount { get; private set; }
    public string RunnerName { get; private set; } = "Cat";

    public event Action<int>? FrameChanged;

    public RunCatService()
    {
        _fetchTimer = new Timer(OnFetchTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start(string runner)
    {
        RunnerName = runner;
        FrameCount = runner switch
        {
            "Parrot" => 10,
            _ => 5
        };
        _currentFrame = 0;

        _samples.Clear();
        _smoothedPressure = 0;
        _lastResourceSampleTick = 0;
        _fetchTimer.Change(0, 200);
        AppLogger.Info($"RunCat started with runner: {runner}");
    }

    public void Stop()
    {
        _fetchTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _samples.Clear();
        _smoothedPressure = 0;
        _lastResourceSampleTick = 0;
        AppLogger.Info("RunCat stopped.");
    }

    public string GetFramePath(int frame)
    {
        string folder = RunnerName.ToLowerInvariant();
        return Path.Combine(AppContext.BaseDirectory, "Assets", "Runners", RunnerName, $"{folder}_{frame}.png");
    }

    private void OnFetchTick(object? state)
    {
        if (_disposed)
        {
            return;
        }

        long nowTick = Environment.TickCount64;
        if (nowTick - _lastResourceSampleTick >= ResourceSamplePeriod.TotalMilliseconds)
        {
            _lastResourceSampleTick = nowTick;
            double pressure = MeasureMemoryPressure();

            lock (_samples)
            {
                _samples.Add(pressure);
                if (_samples.Count > MaxSamples)
                {
                    _samples.RemoveAt(0);
                }

                double averagePressure = _samples.Count > 0 ? _samples.Average() : 0;
                _smoothedPressure = (_smoothedPressure * 0.7) + (averagePressure * 0.3);
            }
        }

        int intervalMs = 500 - (int)Math.Round(_smoothedPressure * 340.0);
        intervalMs = Math.Clamp(intervalMs, 140, 500);

        _currentFrame = (_currentFrame + 1) % FrameCount;
        FrameChanged?.Invoke(_currentFrame);

        try
        {
            _fetchTimer.Change(intervalMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _fetchTimer.Dispose();
    }

    private static double MeasureMemoryPressure()
    {
        double ramPressure = GetRamPressure();
        double gpuPressure = GetGraphicsPressure();

        if (gpuPressure <= 0)
        {
            return ramPressure;
        }

        return Math.Clamp((ramPressure * 0.55) + (gpuPressure * 0.45), 0.0, 1.0);
    }

    private static double GetRamPressure()
    {
        var memoryStatus = MemoryStatusEx.Create();
        if (!GlobalMemoryStatusEx(ref memoryStatus) || memoryStatus.ullTotalPhys == 0)
        {
            return 0;
        }

        double freeRatio = (double)memoryStatus.ullAvailPhys / memoryStatus.ullTotalPhys;
        return 1.0 - Math.Clamp(freeRatio, 0.0, 1.0);
    }

    private static double GetGraphicsPressure()
    {
        if (!GraphicsMemoryMonitor.TryGetFreeMemoryRatio(out double freeRatio))
        {
            return 0;
        }

        return 1.0 - Math.Clamp(freeRatio, 0.0, 1.0);
    }
}
