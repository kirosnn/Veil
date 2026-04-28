using System.Diagnostics;
using System.Runtime;
using Veil.Diagnostics;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed class VeilOptimizationService : IDisposable
{
    private static readonly TimeSpan MaintenanceMinimumInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WorkingSetTrimMinimumInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HeapCompactionMinimumInterval = TimeSpan.FromMinutes(8);
    private const int WorkingSetTrimThresholdMb = 160;
    private const int PrivateMemoryCompactionThresholdMb = 220;

    private readonly object _syncRoot = new();
    private DateTime _lastMaintenanceUtc = DateTime.MinValue;
    private DateTime _lastWorkingSetTrimUtc = DateTime.MinValue;
    private DateTime _lastHeapCompactionUtc = DateTime.MinValue;

    internal void ApplyBackgroundOptimizations()
    {
        lock (_syncRoot)
        {
            DateTime nowUtc = DateTime.UtcNow;
            if (nowUtc - _lastMaintenanceUtc < MaintenanceMinimumInterval)
            {
                return;
            }

            _lastMaintenanceUtc = nowUtc;
            MaintainVeilMemory(nowUtc);
        }
    }

    internal void RestoreNormalOptimizations()
    {
    }

    public void Dispose()
    {
    }

    private void MaintainVeilMemory(DateTime nowUtc)
    {
        try
        {
            using Process currentProcess = Process.GetCurrentProcess();
            double workingSetMb = currentProcess.WorkingSet64 / (1024d * 1024d);
            double privateMemoryMb = currentProcess.PrivateMemorySize64 / (1024d * 1024d);

            bool shouldTrimWorkingSet =
                nowUtc - _lastWorkingSetTrimUtc >= WorkingSetTrimMinimumInterval &&
                workingSetMb >= WorkingSetTrimThresholdMb;

            bool shouldCompactHeap =
                nowUtc - _lastHeapCompactionUtc >= HeapCompactionMinimumInterval &&
                privateMemoryMb >= PrivateMemoryCompactionThresholdMb;

            if (!shouldTrimWorkingSet && !shouldCompactHeap)
            {
                return;
            }

            if (shouldCompactHeap)
            {
                TryCompactMemory();
                _lastHeapCompactionUtc = nowUtc;
            }

            if (shouldTrimWorkingSet)
            {
                try
                {
                    SetProcessWorkingSetSize(currentProcess.Handle, new IntPtr(-1), new IntPtr(-1));
                }
                catch
                {
                }

                _lastWorkingSetTrimUtc = nowUtc;
            }

            AppLogger.Info(
                $"Veil memory maintenance ran. compactHeap={shouldCompactHeap} trimWorkingSet={shouldTrimWorkingSet} ws={workingSetMb:F1}MB private={privateMemoryMb:F1}MB.");
        }
        catch
        {
        }
    }

    private static void TryCompactMemory()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: false, compacting: true);
        }
        catch
        {
        }
    }
}
