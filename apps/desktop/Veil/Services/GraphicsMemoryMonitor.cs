using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Veil.Services;

internal static partial class GraphicsMemoryMonitor
{
    private const uint DXGI_ERROR_NOT_FOUND = 0x887A0002;
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 0x2;
    private const ulong MinimumMeaningfulSegmentBudgetBytes = 256UL * 1024 * 1024;
    private static readonly TimeSpan AdapterCacheDuration = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan EngineUsageCacheDuration = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan EngineCounterRefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly object SyncRoot = new();
    private static DateTime _lastAdapterCacheUtc = DateTime.MinValue;
    private static List<AdapterTelemetry>? _cachedAdapters;
    private static DateTime _lastEngineUsageCacheUtc = DateTime.MinValue;
    private static Dictionary<long, double>? _cachedEngineUsageByLuid;
    private static DateTime _lastEngineCounterRefreshUtc = DateTime.MinValue;
    private static Dictionary<string, PerformanceCounter>? _engineCountersByInstance;

    internal sealed record GpuInfo(
        string Name,
        ulong UsedBytes,
        ulong TotalBytes,
        double MemoryUsagePercent,
        double EngineUsagePercent,
        bool IsIntegrated);

    internal static List<GpuInfo> GetAllGpuInfo()
    {
        List<AdapterTelemetry> adapters = EnumerateAdapters();
        if (adapters.Count == 0)
        {
            return [];
        }

        IReadOnlyDictionary<long, double> engineUsageByLuid = GetEngineUsageByLuid();
        var results = new List<GpuInfo>(adapters.Count);

        foreach (AdapterTelemetry adapter in adapters)
        {
            double memoryUsagePercent = adapter.TotalBytes > 0
                ? (double)adapter.UsedBytes / adapter.TotalBytes * 100
                : 0;

            engineUsageByLuid.TryGetValue(adapter.Luid, out double engineUsagePercent);

            results.Add(new GpuInfo(
                adapter.Name,
                adapter.UsedBytes,
                adapter.TotalBytes,
                Math.Clamp(memoryUsagePercent, 0, 100),
                Math.Clamp(engineUsagePercent, 0, 100),
                adapter.IsIntegrated));
        }

        return results
            .OrderBy(static gpu => gpu.IsIntegrated)
            .ThenByDescending(static gpu => gpu.EngineUsagePercent)
            .ThenByDescending(static gpu => gpu.MemoryUsagePercent)
            .ThenBy(static gpu => gpu.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool TryGetFreeMemoryRatio(out double freeRatio)
    {
        freeRatio = 1.0;
        List<AdapterTelemetry> adapters = EnumerateAdapters();
        if (adapters.Count == 0)
        {
            return false;
        }

        double worstRatio = double.PositiveInfinity;
        bool foundRatio = false;

        foreach (AdapterTelemetry adapter in adapters)
        {
            if (adapter.TotalBytes < MinimumMeaningfulSegmentBudgetBytes && adapter.UsedBytes == 0)
            {
                continue;
            }

            worstRatio = Math.Min(worstRatio, adapter.FreeRatio);
            foundRatio = true;
        }

        if (!foundRatio)
        {
            return false;
        }

        freeRatio = Math.Clamp(worstRatio, 0.0, 1.0);
        return true;
    }

    private static List<AdapterTelemetry> EnumerateAdapters()
    {
        lock (SyncRoot)
        {
            if (_cachedAdapters is not null &&
                DateTime.UtcNow - _lastAdapterCacheUtc < AdapterCacheDuration)
            {
                return _cachedAdapters;
            }
        }

        var uniqueAdapters = new Dictionary<long, AdapterTelemetry>();
        IntPtr factoryPtr = IntPtr.Zero;

        try
        {
            Guid factoryGuid = typeof(IDXGIFactory1).GUID;
            int hr = CreateDXGIFactory1(in factoryGuid, out factoryPtr);
            if (hr < 0 || factoryPtr == IntPtr.Zero)
            {
                return [];
            }

            var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPtr);

            for (uint index = 0; ; index++)
            {
                hr = factory.EnumAdapters1(index, out IDXGIAdapter1? adapter);
                if ((uint)hr == DXGI_ERROR_NOT_FOUND)
                {
                    break;
                }

                if (hr < 0 || adapter == null)
                {
                    continue;
                }

                try
                {
                    adapter.GetDesc1(out DXGI_ADAPTER_DESC1 desc);
                    if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0)
                    {
                        continue;
                    }

                    if (!TryCreateAdapterTelemetry(adapter, desc, out AdapterTelemetry telemetry))
                    {
                        continue;
                    }

                    if (uniqueAdapters.TryGetValue(telemetry.Luid, out AdapterTelemetry existing))
                    {
                        uniqueAdapters[telemetry.Luid] = PickPreferredAdapter(existing, telemetry);
                    }
                    else
                    {
                        uniqueAdapters.Add(telemetry.Luid, telemetry);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        catch
        {
            return [];
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero)
            {
                Marshal.Release(factoryPtr);
            }
        }

        List<AdapterTelemetry> adapters = uniqueAdapters.Values.ToList();

        lock (SyncRoot)
        {
            _cachedAdapters = adapters;
            _lastAdapterCacheUtc = DateTime.UtcNow;
        }

        return adapters;
    }

    private static bool TryCreateAdapterTelemetry(IDXGIAdapter1 adapter, DXGI_ADAPTER_DESC1 desc, out AdapterTelemetry telemetry)
    {
        bool isIntegrated = desc.DedicatedVideoMemory == 0;
        string name = desc.Description?.Trim('\0').Trim() ?? "GPU";

        if (!TryGetAdapterMemorySnapshot(adapter, desc, out AdapterMemorySnapshot snapshot))
        {
            telemetry = new AdapterTelemetry(
                desc.AdapterLuid,
                name,
                0,
                0,
                1.0,
                isIntegrated,
                0);
            return true;
        }

        telemetry = new AdapterTelemetry(
            desc.AdapterLuid,
            name,
            snapshot.UsedBytes,
            snapshot.TotalBytes,
            snapshot.FreeRatio,
            isIntegrated,
            GetAdapterPreferenceScore(snapshot, isIntegrated));
        return true;
    }

    private static AdapterTelemetry PickPreferredAdapter(AdapterTelemetry current, AdapterTelemetry candidate)
    {
        if (candidate.PreferenceScore != current.PreferenceScore)
        {
            return candidate.PreferenceScore > current.PreferenceScore ? candidate : current;
        }

        if (candidate.TotalBytes != current.TotalBytes)
        {
            return candidate.TotalBytes > current.TotalBytes ? candidate : current;
        }

        return candidate.UsedBytes > current.UsedBytes ? candidate : current;
    }

    private static int GetAdapterPreferenceScore(AdapterMemorySnapshot snapshot, bool isIntegrated)
    {
        int score = 0;
        if (snapshot.TotalBytes > 0)
        {
            score += 2;
        }

        if (snapshot.TotalBytes >= MinimumMeaningfulSegmentBudgetBytes)
        {
            score += 2;
        }

        if (snapshot.UsedBytes > 0)
        {
            score += 1;
        }

        if (!isIntegrated)
        {
            score += 1;
        }

        return score;
    }

    private static IReadOnlyDictionary<long, double> GetEngineUsageByLuid()
    {
        lock (SyncRoot)
        {
            if (_cachedEngineUsageByLuid is not null &&
                DateTime.UtcNow - _lastEngineUsageCacheUtc < EngineUsageCacheDuration)
            {
                return _cachedEngineUsageByLuid;
            }
        }

        var usageByLuid = new Dictionary<long, double>();

        try
        {
            if (!PerformanceCounterCategory.Exists("GPU Engine"))
            {
                return usageByLuid;
            }

            Dictionary<string, PerformanceCounter> countersByInstance = GetOrRefreshEngineCounters();
            foreach ((string instanceName, PerformanceCounter counter) in countersByInstance)
            {
                if (!TryParseLuidFromGpuEngineInstance(instanceName, out long luid))
                {
                    continue;
                }

                try
                {
                    double value = counter.NextValue();
                    if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                    {
                        continue;
                    }

                    usageByLuid[luid] = usageByLuid.TryGetValue(luid, out double current)
                        ? current + value
                        : value;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        if (usageByLuid.Count == 0)
        {
            lock (SyncRoot)
            {
                _cachedEngineUsageByLuid = usageByLuid;
                _lastEngineUsageCacheUtc = DateTime.UtcNow;
            }

            return usageByLuid;
        }

        Dictionary<long, double> clampedUsage = usageByLuid.ToDictionary(
            static pair => pair.Key,
            static pair => Math.Clamp(pair.Value, 0, 100));

        lock (SyncRoot)
        {
            _cachedEngineUsageByLuid = clampedUsage;
            _lastEngineUsageCacheUtc = DateTime.UtcNow;
        }

        return clampedUsage;
    }

    private static Dictionary<string, PerformanceCounter> GetOrRefreshEngineCounters()
    {
        lock (SyncRoot)
        {
            if (_engineCountersByInstance is not null &&
                DateTime.UtcNow - _lastEngineCounterRefreshUtc < EngineCounterRefreshInterval)
            {
                return _engineCountersByInstance;
            }
        }

        var refreshedCounters = new Dictionary<string, PerformanceCounter>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            foreach (string instanceName in category.GetInstanceNames())
            {
                try
                {
                    var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instanceName, readOnly: true);
                    _ = counter.NextValue();
                    refreshedCounters[instanceName] = counter;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        lock (SyncRoot)
        {
            if (_engineCountersByInstance is not null)
            {
                foreach (PerformanceCounter counter in _engineCountersByInstance.Values)
                {
                    counter.Dispose();
                }
            }

            _engineCountersByInstance = refreshedCounters;
            _lastEngineCounterRefreshUtc = DateTime.UtcNow;
            return _engineCountersByInstance;
        }
    }

    private static bool TryParseLuidFromGpuEngineInstance(string instanceName, out long luid)
    {
        luid = 0;

        const string marker = "luid_0x";
        int markerIndex = instanceName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        int highStart = markerIndex + marker.Length;
        int separatorIndex = instanceName.IndexOf("_0x", highStart, StringComparison.OrdinalIgnoreCase);
        if (separatorIndex < 0)
        {
            return false;
        }

        string highPartHex = instanceName[highStart..separatorIndex];
        int lowStart = separatorIndex + 3;
        int lowEnd = instanceName.IndexOf('_', lowStart);
        if (lowEnd < 0)
        {
            lowEnd = instanceName.Length;
        }

        string lowPartHex = instanceName[lowStart..lowEnd];
        if (!uint.TryParse(highPartHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint highPart) ||
            !uint.TryParse(lowPartHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint lowPart))
        {
            return false;
        }

        luid = unchecked((long)(((ulong)highPart << 32) | lowPart));
        return true;
    }

    private static bool TryGetAdapterMemorySnapshot(IDXGIAdapter1 adapter, DXGI_ADAPTER_DESC1 desc, out AdapterMemorySnapshot snapshot)
    {
        ulong localBudget = 0;
        ulong localUsage = 0;
        bool hasLocalSegment = false;
        bool hasMeaningfulLocalSegment = false;

        ulong nonLocalBudget = 0;
        ulong nonLocalUsage = 0;
        bool hasNonLocalSegment = false;
        bool hasMeaningfulNonLocalSegment = false;

        if (adapter is IDXGIAdapter3 adapter3)
        {
            foreach (DXGI_MEMORY_SEGMENT_GROUP segmentGroup in Enum.GetValues<DXGI_MEMORY_SEGMENT_GROUP>())
            {
                int hr = adapter3.QueryVideoMemoryInfo(0, segmentGroup, out DXGI_QUERY_VIDEO_MEMORY_INFO info);
                if (hr < 0 || info.Budget == 0)
                {
                    continue;
                }

                switch (segmentGroup)
                {
                    case DXGI_MEMORY_SEGMENT_GROUP.Local:
                        localBudget += info.Budget;
                        localUsage += info.CurrentUsage;
                        hasLocalSegment = true;
                        hasMeaningfulLocalSegment |= info.Budget >= MinimumMeaningfulSegmentBudgetBytes;
                        break;
                    case DXGI_MEMORY_SEGMENT_GROUP.NonLocal:
                        nonLocalBudget += info.Budget;
                        nonLocalUsage += info.CurrentUsage;
                        hasNonLocalSegment = true;
                        hasMeaningfulNonLocalSegment |= info.Budget >= MinimumMeaningfulSegmentBudgetBytes;
                        break;
                }
            }
        }

        bool isIntegratedAdapter = desc.DedicatedVideoMemory == 0;
        ulong totalBudget;
        ulong totalUsage;

        if (isIntegratedAdapter)
        {
            if (hasNonLocalSegment && (hasMeaningfulNonLocalSegment || !hasMeaningfulLocalSegment))
            {
                totalBudget = nonLocalBudget;
                totalUsage = nonLocalUsage;
            }
            else
            {
                totalBudget = localBudget;
                totalUsage = localUsage;
            }
        }
        else if (hasLocalSegment && (hasMeaningfulLocalSegment || !hasMeaningfulNonLocalSegment))
        {
            totalBudget = localBudget;
            totalUsage = localUsage;
        }
        else
        {
            totalBudget = localBudget + nonLocalBudget;
            totalUsage = localUsage + nonLocalUsage;
        }

        if (totalBudget == 0)
        {
            ulong dedicated = (ulong)(nuint)desc.DedicatedVideoMemory;
            ulong shared = (ulong)(nuint)desc.SharedSystemMemory;
            totalBudget = isIntegratedAdapter
                ? shared
                : dedicated > 0 ? dedicated : shared;
        }

        if (totalBudget == 0)
        {
            snapshot = default;
            return false;
        }

        ulong freeBytes = totalBudget > totalUsage
            ? totalBudget - totalUsage
            : 0;
        double freeRatio = (double)freeBytes / totalBudget;

        snapshot = new AdapterMemorySnapshot(
            totalUsage,
            totalBudget,
            Math.Clamp(freeRatio, 0.0, 1.0));
        return true;
    }

    private readonly record struct AdapterMemorySnapshot(ulong UsedBytes, ulong TotalBytes, double FreeRatio);
    private readonly record struct AdapterTelemetry(
        long Luid,
        string Name,
        ulong UsedBytes,
        ulong TotalBytes,
        double FreeRatio,
        bool IsIntegrated,
        int PreferenceScore);

    [LibraryImport("dxgi.dll", EntryPoint = "CreateDXGIFactory1")]
    private static partial int CreateDXGIFactory1(in Guid riid, out IntPtr ppFactory);

    private enum DXGI_MEMORY_SEGMENT_GROUP
    {
        Local = 0,
        NonLocal = 1
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_QUERY_VIDEO_MEMORY_INFO
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }

    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        int SetPrivateData();
        int SetPrivateDataInterface();
        int GetPrivateData();
        int GetParent();
        int EnumAdapters(uint adapter, out IntPtr ppAdapter);
        int MakeWindowAssociation();
        int GetWindowAssociation();
        int CreateSwapChain();
        int CreateSoftwareAdapter();
        int EnumAdapters1(uint adapter, out IDXGIAdapter1 ppAdapter);
        int IsCurrent();
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        int SetPrivateData();
        int SetPrivateDataInterface();
        int GetPrivateData();
        int GetParent();
        int EnumOutputs();
        int GetDesc(out IntPtr pDesc);
        int CheckInterfaceSupport();
        int GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
    }

    [ComImport]
    [Guid("645967A4-1392-4310-A798-8053CE3E93FD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter3 : IDXGIAdapter1
    {
        new int SetPrivateData();
        new int SetPrivateDataInterface();
        new int GetPrivateData();
        new int GetParent();
        new int EnumOutputs();
        new int GetDesc(out IntPtr pDesc);
        new int CheckInterfaceSupport();
        new int GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
        int RegisterHardwareContentProtectionTeardownStatusEvent();
        void UnregisterHardwareContentProtectionTeardownStatus();
        int QueryVideoMemoryInfo(uint nodeIndex, DXGI_MEMORY_SEGMENT_GROUP memorySegmentGroup, out DXGI_QUERY_VIDEO_MEMORY_INFO pVideoMemoryInfo);
        int SetVideoMemoryReservation();
        int RegisterVideoMemoryBudgetChangeNotificationEvent();
        void UnregisterVideoMemoryBudgetChangeNotification();
    }
}
