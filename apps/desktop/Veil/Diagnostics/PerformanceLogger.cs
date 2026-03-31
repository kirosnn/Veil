using System.Diagnostics;

namespace Veil.Diagnostics;

internal static class PerformanceLogger
{
    private sealed class MetricBucket
    {
        internal long Count;
        internal long TotalTicks;
        internal long MaxTicks;
        internal long SlowCount;
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly string _name;
        private readonly long _startTimestamp;
        private readonly double _slowThresholdMs;

        internal Scope(string name, double slowThresholdMs)
        {
            _name = name;
            _slowThresholdMs = slowThresholdMs;
            _startTimestamp = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - _startTimestamp;
            Record(_name, elapsedTicks, _slowThresholdMs);
        }
    }

    private static readonly Lock Sync = new();
    private static readonly Dictionary<string, MetricBucket> Metrics = new(StringComparer.Ordinal);
    private static CancellationTokenSource? _cts;
    private static Task? _monitorTask;
    private static TimeSpan _lastTotalProcessorTime;
    private static long _lastFlushTimestamp;

    internal static void Start()
    {
        lock (Sync)
        {
            if (_cts is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            using Process current = Process.GetCurrentProcess();
            _lastTotalProcessorTime = current.TotalProcessorTime;
            _lastFlushTimestamp = Stopwatch.GetTimestamp();
            _monitorTask = Task.Run(() => MonitorLoopAsync(_cts.Token));
            AppLogger.Info("Performance logger started.");
        }
    }

    internal static void Stop()
    {
        CancellationTokenSource? cts;
        Task? monitorTask;

        lock (Sync)
        {
            cts = _cts;
            monitorTask = _monitorTask;
            _cts = null;
            _monitorTask = null;
        }

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            monitorTask?.Wait(1000);
        }
        catch
        {
        }
        finally
        {
            cts.Dispose();
        }

        Flush(force: true);
        AppLogger.Info("Performance logger stopped.");
    }

    internal static Scope Measure(string name, double slowThresholdMs = 0)
    {
        return new Scope(name, slowThresholdMs);
    }

    internal static void RecordMilliseconds(string name, double elapsedMs, double slowThresholdMs = 0)
    {
        long elapsedTicks = (long)Math.Round(elapsedMs * Stopwatch.Frequency / 1000d);
        Record(name, elapsedTicks, slowThresholdMs);
    }

    private static void Record(string name, long elapsedTicks, double slowThresholdMs)
    {
        lock (Sync)
        {
            if (!Metrics.TryGetValue(name, out MetricBucket? bucket))
            {
                bucket = new MetricBucket();
                Metrics[name] = bucket;
            }

            bucket.Count++;
            bucket.TotalTicks += elapsedTicks;
            bucket.MaxTicks = Math.Max(bucket.MaxTicks, elapsedTicks);

            double elapsedMs = elapsedTicks * 1000d / Stopwatch.Frequency;
            if (slowThresholdMs > 0 && elapsedMs >= slowThresholdMs)
            {
                bucket.SlowCount++;
            }
        }
    }

    private static async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                Flush(force: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Flush(bool force)
    {
        Dictionary<string, MetricBucket>? snapshot = null;
        long nowTimestamp = Stopwatch.GetTimestamp();
        double intervalSeconds;

        lock (Sync)
        {
            if (Metrics.Count == 0 && !force)
            {
                intervalSeconds = (nowTimestamp - _lastFlushTimestamp) / (double)Stopwatch.Frequency;
                if (intervalSeconds < 30)
                {
                    return;
                }
            }

            intervalSeconds = Math.Max(0.001, (nowTimestamp - _lastFlushTimestamp) / (double)Stopwatch.Frequency);
            _lastFlushTimestamp = nowTimestamp;

            if (Metrics.Count > 0)
            {
                snapshot = Metrics.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
                Metrics.Clear();
            }
        }

        using Process current = Process.GetCurrentProcess();
        TimeSpan totalProcessorTime = current.TotalProcessorTime;
        TimeSpan cpuDelta = totalProcessorTime - _lastTotalProcessorTime;
        _lastTotalProcessorTime = totalProcessorTime;

        double cpuPercent = cpuDelta.TotalMilliseconds / (intervalSeconds * 10 * Environment.ProcessorCount);
        double workingSetMb = current.WorkingSet64 / 1024d / 1024d;
        double privateMb = current.PrivateMemorySize64 / 1024d / 1024d;

        AppLogger.Info(
            $"Perf snapshot interval={intervalSeconds:F1}s cpu={cpuPercent:F1}% ws={workingSetMb:F1}MB private={privateMb:F1}MB threads={current.Threads.Count} handles={current.HandleCount}");

        if (snapshot is null || snapshot.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<string, MetricBucket> pair in snapshot.OrderByDescending(static pair => pair.Value.TotalTicks))
        {
            double totalMs = pair.Value.TotalTicks * 1000d / Stopwatch.Frequency;
            double avgMs = totalMs / Math.Max(1, pair.Value.Count);
            double maxMs = pair.Value.MaxTicks * 1000d / Stopwatch.Frequency;
            AppLogger.Info(
                $"Perf metric name={pair.Key} count={pair.Value.Count} avg={avgMs:F3}ms max={maxMs:F3}ms total={totalMs:F1}ms slow={pair.Value.SlowCount}");
        }
    }
}
