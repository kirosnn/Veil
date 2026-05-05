using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Veil.Services;

internal static partial class GraphicsMemoryMonitor
{
    private const uint DXGI_ERROR_NOT_FOUND = 0x887A0002;
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 0x2;
    private const ulong MinimumMeaningfulSegmentBudgetBytes = 256UL * 1024 * 1024;
    private const ulong MinimumFallbackAdapterUsageBytes = 16UL * 1024 * 1024;
    private static readonly TimeSpan AdapterCacheDuration = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan EngineUsageCacheDuration = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan EngineCounterRefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly ulong FallbackAdapterBudgetBytes = 1024UL * 1024 * 1024;
    private static readonly ulong FallbackAdapterBudgetStepBytes = 1024UL * 1024 * 1024;
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
        bool IsIntegrated,
        double? TemperatureCelsius);

    internal static List<GpuInfo> GetAllGpuInfo()
    {
        List<AdapterTelemetry> adapters = EnumerateAdapters();
        if (adapters.Count == 0)
        {
            adapters = GetFallbackAdapterTelemetryFromCounters();
            if (adapters.Count == 0)
            {
                return GetSensorOnlyGpuInfo();
            }
        }

        HardwareSensorMonitor.HardwareSnapshot sensorSnapshot = HardwareSensorMonitor.GetSnapshot();
        IReadOnlyDictionary<long, double> engineUsageByLuid = GetEngineUsageByLuid();
        var results = new List<GpuInfo>(adapters.Count);

        foreach (AdapterTelemetry adapter in adapters)
        {
            HardwareSensorMonitor.GpuSensorInfo? sensorInfo = FindMatchingGpuSensor(adapter.Name, sensorSnapshot.Gpus);
            ulong usedBytes = sensorInfo?.UsedMemoryBytes ?? adapter.UsedBytes;
            ulong totalBytes = sensorInfo?.TotalMemoryBytes ?? adapter.TotalBytes;
            if (totalBytes < usedBytes)
            {
                totalBytes = adapter.TotalBytes >= usedBytes
                    ? adapter.TotalBytes
                    : EstimateFallbackAdapterBudget(usedBytes);
            }

            double memoryUsagePercent = totalBytes > 0
                ? (double)usedBytes / totalBytes * 100
                : 0;

            engineUsageByLuid.TryGetValue(adapter.Luid, out double engineUsagePercent);
            if (sensorInfo?.LoadPercent is double sensorLoadPercent)
            {
                engineUsagePercent = sensorLoadPercent;
            }

            results.Add(new GpuInfo(
                adapter.Name,
                usedBytes,
                totalBytes,
                Math.Clamp(memoryUsagePercent, 0, 100),
                Math.Clamp(engineUsagePercent, 0, 100),
                sensorInfo?.IsIntegrated ?? adapter.IsIntegrated,
                sensorInfo?.TemperatureCelsius));
        }

        return results
            .OrderBy(static gpu => gpu.IsIntegrated)
            .ThenByDescending(static gpu => gpu.EngineUsagePercent)
            .ThenByDescending(static gpu => gpu.MemoryUsagePercent)
            .ThenBy(static gpu => gpu.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<GpuInfo> GetSensorOnlyGpuInfo()
    {
        HardwareSensorMonitor.HardwareSnapshot snapshot = HardwareSensorMonitor.GetSnapshot();
        var results = new List<GpuInfo>(snapshot.Gpus.Count);

        foreach (HardwareSensorMonitor.GpuSensorInfo gpu in snapshot.Gpus)
        {
            ulong usedBytes = gpu.UsedMemoryBytes ?? 0;
            ulong totalBytes = gpu.TotalMemoryBytes ?? 0;
            if (totalBytes < usedBytes)
            {
                totalBytes = EstimateFallbackAdapterBudget(usedBytes);
            }

            double memoryUsagePercent = totalBytes > 0
                ? (double)usedBytes / totalBytes * 100
                : 0;

            results.Add(new GpuInfo(
                gpu.Name,
                usedBytes,
                totalBytes,
                Math.Clamp(memoryUsagePercent, 0, 100),
                Math.Clamp(gpu.LoadPercent ?? 0, 0, 100),
                gpu.IsIntegrated ?? IsIntegratedGpuName(gpu.Name),
                gpu.TemperatureCelsius));
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
            adapters = GetFallbackAdapterTelemetryFromCounters();
            if (adapters.Count == 0)
            {
                return false;
            }
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
        string name = desc.Description?.Trim('\0').Trim() ?? "GPU";
        bool isIntegrated = IsIntegratedAdapter(desc, name);

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

        string adapterName = desc.Description?.Trim('\0').Trim() ?? string.Empty;
        bool isIntegratedAdapter = IsIntegratedAdapter(desc, adapterName);
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

    private readonly record struct AdapterCounterSnapshot(string Name)
    {
        public ulong DedicatedUsageBytes { get; init; }
        public ulong SharedUsageBytes { get; init; }
        public ulong TotalCommittedBytes { get; init; }
    }

    private static bool IsIntegratedAdapter(DXGI_ADAPTER_DESC1 desc, string name)
    {
        if (desc.DedicatedVideoMemory == 0)
        {
            return true;
        }

        return IsIntegratedGpuName(name);
    }

    private static bool IsIntegratedGpuName(string name)
    {
        string normalizedName = name.Trim();
        if (normalizedName.Contains("Intel", StringComparison.OrdinalIgnoreCase)
            && (normalizedName.Contains("UHD", StringComparison.OrdinalIgnoreCase)
                || normalizedName.Contains("Iris", StringComparison.OrdinalIgnoreCase)
                || normalizedName.Contains("Xe", StringComparison.OrdinalIgnoreCase)
                || normalizedName.Contains("Graphics", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (normalizedName.Contains("AMD Radeon", StringComparison.OrdinalIgnoreCase)
            && normalizedName.Contains("Graphics", StringComparison.OrdinalIgnoreCase)
            && !normalizedName.Contains("RX", StringComparison.OrdinalIgnoreCase)
            && !normalizedName.Contains("Pro", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalizedName.Contains("Radeon 780M", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("Radeon 760M", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("Radeon 740M", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("Radeon 680M", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("Radeon 660M", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("Radeon Vega", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static HardwareSensorMonitor.GpuSensorInfo? FindMatchingGpuSensor(
        string adapterName,
        IReadOnlyList<HardwareSensorMonitor.GpuSensorInfo> sensorGpus)
    {
        if (sensorGpus.Count == 0)
        {
            return null;
        }

        string normalizedAdapterName = NormalizeGpuNameForMatching(adapterName);
        HardwareSensorMonitor.GpuSensorInfo? exactMatch = sensorGpus.FirstOrDefault(gpu =>
            NormalizeGpuNameForMatching(gpu.Name) == normalizedAdapterName);
        if (exactMatch != null)
        {
            return exactMatch;
        }

        return sensorGpus.FirstOrDefault(gpu =>
        {
            string normalizedSensorName = NormalizeGpuNameForMatching(gpu.Name);
            return normalizedAdapterName.Contains(normalizedSensorName, StringComparison.OrdinalIgnoreCase) ||
                normalizedSensorName.Contains(normalizedAdapterName, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string NormalizeGpuNameForMatching(string name)
    {
        return name
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("GPU", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Graphics", "Graphics", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ")
            .Replace("  ", " ")
            .Trim()
            .ToUpperInvariant();
    }

    private static List<AdapterTelemetry> GetFallbackAdapterTelemetryFromCounters()
    {
        var snapshotsByLuid = new Dictionary<long, AdapterCounterSnapshot>();

        AddAdapterCounterSamples("Dedicated Usage", snapshotsByLuid);
        AddAdapterCounterSamples("Shared Usage", snapshotsByLuid);
        AddAdapterCounterSamples("Total Committed", snapshotsByLuid);

        string[] adapterNames = GetFallbackAdapterNames();
        int adapterNameIndex = 0;
        var results = new List<AdapterTelemetry>(snapshotsByLuid.Count);

        foreach ((long luid, AdapterCounterSnapshot snapshot) in snapshotsByLuid
            .OrderByDescending(static pair => Math.Max(
                pair.Value.TotalCommittedBytes,
                pair.Value.DedicatedUsageBytes + pair.Value.SharedUsageBytes)))
        {
            ulong usedBytes = snapshot.TotalCommittedBytes > 0
                ? snapshot.TotalCommittedBytes
                : snapshot.DedicatedUsageBytes + snapshot.SharedUsageBytes;

            if (usedBytes < MinimumFallbackAdapterUsageBytes)
            {
                continue;
            }

            string name = adapterNameIndex < adapterNames.Length
                ? adapterNames[adapterNameIndex++]
                : snapshot.Name;

            ulong totalBytes = EstimateFallbackAdapterBudget(usedBytes);
            double freeRatio = totalBytes > usedBytes
                ? (double)(totalBytes - usedBytes) / totalBytes
                : 0;

            results.Add(new AdapterTelemetry(
                luid,
                name,
                usedBytes,
                totalBytes,
                Math.Clamp(freeRatio, 0.0, 1.0),
                true,
                1));
        }

        return results;
    }

    private static ulong EstimateFallbackAdapterBudget(ulong usedBytes)
    {
        if (usedBytes <= FallbackAdapterBudgetBytes)
        {
            return FallbackAdapterBudgetBytes;
        }

        ulong steps = (usedBytes + FallbackAdapterBudgetStepBytes - 1) / FallbackAdapterBudgetStepBytes;
        return Math.Max(FallbackAdapterBudgetBytes, steps * FallbackAdapterBudgetStepBytes);
    }

    private static string[] GetFallbackAdapterNames()
    {
        var names = new List<string>();

        try
        {
            using RegistryKey? pciKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
            if (pciKey == null)
            {
                return [];
            }

            foreach (string deviceKeyName in pciKey.GetSubKeyNames())
            {
                using RegistryKey? deviceKey = pciKey.OpenSubKey(deviceKeyName);
                if (deviceKey == null)
                {
                    continue;
                }

                foreach (string instanceKeyName in deviceKey.GetSubKeyNames())
                {
                    using RegistryKey? instanceKey = deviceKey.OpenSubKey(instanceKeyName);
                    if (instanceKey == null)
                    {
                        continue;
                    }

                    if (!IsDisplayRegistryDevice(instanceKey))
                    {
                        continue;
                    }

                    string name = NormalizeRegistryDeviceName(
                        instanceKey.GetValue("FriendlyName") as string
                        ?? instanceKey.GetValue("DeviceDesc") as string);

                    if (string.IsNullOrWhiteSpace(name) ||
                        names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    names.Add(name);
                }
            }
        }
        catch
        {
        }

        return names.ToArray();
    }

    private static bool IsDisplayRegistryDevice(RegistryKey instanceKey)
    {
        string? className = instanceKey.GetValue("Class") as string;
        if (string.Equals(className, "Display", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? classGuid = instanceKey.GetValue("ClassGUID") as string;
        if (string.Equals(classGuid, "{4d36e968-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (instanceKey.GetValue("HardwareID") is string[] hardwareIds)
        {
            return hardwareIds.Any(static hardwareId =>
                hardwareId.Contains("&CC_0300", StringComparison.OrdinalIgnoreCase) ||
                hardwareId.Contains("&CC_0302", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static string NormalizeRegistryDeviceName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string name = value.Trim();
        int separatorIndex = name.LastIndexOf(';');
        if (separatorIndex >= 0 && separatorIndex + 1 < name.Length)
        {
            name = name[(separatorIndex + 1)..].Trim();
        }

        return name.Replace(" (TM)", "(TM)", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddAdapterCounterSamples(string counterName, Dictionary<long, AdapterCounterSnapshot> snapshotsByLuid)
    {
        try
        {
            var category = new PerformanceCounterCategory("GPU Adapter Memory");
            foreach (string instanceName in category.GetInstanceNames())
            {
                if (!TryParseLuidFromGpuEngineInstance(instanceName, out long luid))
                {
                    continue;
                }

                ulong value;
                try
                {
                    using var counter = new PerformanceCounter("GPU Adapter Memory", counterName, instanceName, readOnly: true);
                    long rawValue = counter.RawValue;
                    value = rawValue > 0 ? (ulong)rawValue : 0;
                }
                catch
                {
                    continue;
                }

                snapshotsByLuid.TryGetValue(luid, out AdapterCounterSnapshot snapshot);
                snapshot = string.IsNullOrWhiteSpace(snapshot.Name)
                    ? new AdapterCounterSnapshot($"GPU Adapter {FormatLuid(luid)}")
                    : snapshot;

                snapshot = counterName switch
                {
                    "Dedicated Usage" => snapshot with { DedicatedUsageBytes = value },
                    "Shared Usage" => snapshot with { SharedUsageBytes = value },
                    "Total Committed" => snapshot with { TotalCommittedBytes = value },
                    _ => snapshot
                };

                snapshotsByLuid[luid] = snapshot;
            }
        }
        catch
        {
        }
    }

    private static string FormatLuid(long luid)
    {
        ulong value = unchecked((ulong)luid);
        uint high = (uint)(value >> 32);
        uint low = (uint)(value & 0xffffffff);
        return $"0x{high:x8}:0x{low:x8}";
    }

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC2
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
        public int GraphicsPreemptionGranularity;
        public int ComputePreemptionGranularity;
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
        int GetDesc2(out DXGI_ADAPTER_DESC2 pDesc);
        int RegisterHardwareContentProtectionTeardownStatusEvent();
        void UnregisterHardwareContentProtectionTeardownStatus();
        int QueryVideoMemoryInfo(uint nodeIndex, DXGI_MEMORY_SEGMENT_GROUP memorySegmentGroup, out DXGI_QUERY_VIDEO_MEMORY_INFO pVideoMemoryInfo);
        int SetVideoMemoryReservation();
        int RegisterVideoMemoryBudgetChangeNotificationEvent();
        void UnregisterVideoMemoryBudgetChangeNotification();
    }
}
