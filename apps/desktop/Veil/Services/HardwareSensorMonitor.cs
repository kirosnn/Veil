using LibreHardwareMonitor.Hardware;

namespace Veil.Services;

internal static class HardwareSensorMonitor
{
    private static readonly TimeSpan SnapshotCacheDuration = TimeSpan.FromMilliseconds(1200);
    private static readonly object SyncRoot = new();
    private static Computer? _computer;
    private static DateTime _lastSnapshotUtc = DateTime.MinValue;
    private static HardwareSnapshot _cachedSnapshot = HardwareSnapshot.Empty;
    private static bool _openFailed;

    internal sealed record HardwareSnapshot(double? CpuTemperatureCelsius, IReadOnlyList<GpuSensorInfo> Gpus)
    {
        internal static readonly HardwareSnapshot Empty = new(null, []);
    }

    internal sealed record GpuSensorInfo(
        string Name,
        double? TemperatureCelsius,
        double? LoadPercent,
        ulong? UsedMemoryBytes,
        ulong? TotalMemoryBytes,
        bool? IsIntegrated);

    internal static HardwareSnapshot GetSnapshot()
    {
        lock (SyncRoot)
        {
            if (DateTime.UtcNow - _lastSnapshotUtc < SnapshotCacheDuration)
            {
                return _cachedSnapshot;
            }

            _cachedSnapshot = ReadSnapshot();
            _lastSnapshotUtc = DateTime.UtcNow;
            return _cachedSnapshot;
        }
    }

    private static HardwareSnapshot ReadSnapshot()
    {
        Computer? computer = GetComputer();
        if (computer == null)
        {
            return HardwareSnapshot.Empty;
        }

        try
        {
            foreach (IHardware hardware in computer.Hardware)
            {
                UpdateHardwareTree(hardware);
            }

            double? cpuTemperature = GetCpuTemperature(computer.Hardware);
            List<GpuSensorInfo> gpus = GetGpuSensors(computer.Hardware);
            return new HardwareSnapshot(cpuTemperature, gpus);
        }
        catch
        {
            return HardwareSnapshot.Empty;
        }
    }

    private static Computer? GetComputer()
    {
        if (_openFailed)
        {
            return null;
        }

        if (_computer != null)
        {
            return _computer;
        }

        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true
            };
            _computer.Open();
            return _computer;
        }
        catch
        {
            _computer = null;
            _openFailed = true;
            return null;
        }
    }

    private static void UpdateHardwareTree(IHardware hardware)
    {
        hardware.Update();
        foreach (IHardware subHardware in hardware.SubHardware)
        {
            UpdateHardwareTree(subHardware);
        }
    }

    private static double? GetCpuTemperature(IEnumerable<IHardware> hardware)
    {
        List<ISensor> cpuTemperatureSensors = FlattenHardware(hardware)
            .Where(static item => item.Hardware.HardwareType == HardwareType.Cpu)
            .SelectMany(static item => item.Hardware.Sensors)
            .Where(IsValidTemperatureSensor)
            .ToList();

        double? cpuTemperature = PickPreferredTemperature(cpuTemperatureSensors, "package", "tctl", "tdie", "core max", "ccd");
        if (cpuTemperature.HasValue)
        {
            return cpuTemperature;
        }

        List<ISensor> boardCpuTemperatureSensors = FlattenHardware(hardware)
            .Where(static item => item.Hardware.HardwareType == HardwareType.Motherboard ||
                item.Hardware.HardwareType == HardwareType.SuperIO)
            .SelectMany(static item => item.Hardware.Sensors)
            .Where(static sensor => IsValidTemperatureSensor(sensor) &&
                sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return PickPreferredTemperature(boardCpuTemperatureSensors, "cpu", "package", "socket");
    }

    private static List<GpuSensorInfo> GetGpuSensors(IEnumerable<IHardware> hardware)
    {
        var gpus = new List<GpuSensorInfo>();

        foreach (IHardware gpu in FlattenHardware(hardware)
            .Select(static item => item.Hardware)
            .Where(IsGpuHardware))
        {
            IReadOnlyList<ISensor> sensors = gpu.Sensors;
            double? temperature = PickPreferredTemperature(
                sensors.Where(IsValidTemperatureSensor),
                "hot spot",
                "junction",
                "core",
                "gpu");
            double? load = PickPreferredLoad(sensors);
            ulong? usedMemory = PickMemorySensorBytes(sensors, "used", "dedicated usage", "allocated");
            ulong? totalMemory = PickMemorySensorBytes(sensors, "total", "dedicated total");

            gpus.Add(new GpuSensorInfo(
                gpu.Name.Trim(),
                temperature,
                load,
                usedMemory,
                totalMemory,
                gpu.HardwareType == HardwareType.GpuIntel));
        }

        return gpus;
    }

    private static IEnumerable<(IHardware Hardware, IHardware? Parent)> FlattenHardware(IEnumerable<IHardware> hardware)
    {
        foreach (IHardware item in hardware)
        {
            yield return (item, null);

            foreach ((IHardware Hardware, IHardware? Parent) child in FlattenHardware(item.SubHardware))
            {
                yield return (child.Hardware, item);
            }
        }
    }

    private static bool IsGpuHardware(IHardware hardware)
    {
        return hardware.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia;
    }

    private static bool IsValidTemperatureSensor(ISensor sensor)
    {
        return sensor.SensorType == SensorType.Temperature &&
            sensor.Value is >= 0 and <= 125;
    }

    private static double? PickPreferredTemperature(IEnumerable<ISensor> sensors, params string[] preferredNames)
    {
        List<ISensor> validSensors = sensors
            .Where(IsValidTemperatureSensor)
            .ToList();

        foreach (string preferredName in preferredNames)
        {
            ISensor? sensor = validSensors.FirstOrDefault(item =>
                item.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase));
            if (sensor?.Value is float value)
            {
                return value;
            }
        }

        return validSensors
            .Select(static sensor => sensor.Value)
            .Where(static value => value.HasValue)
            .Select(static value => (double)value!.Value)
            .DefaultIfEmpty(double.NaN)
            .Max() is double fallback && !double.IsNaN(fallback)
                ? fallback
                : null;
    }

    private static double? PickPreferredLoad(IEnumerable<ISensor> sensors)
    {
        List<ISensor> loadSensors = sensors
            .Where(static sensor => sensor.SensorType == SensorType.Load &&
                sensor.Value is >= 0 and <= 100)
            .ToList();

        foreach (string preferredName in new[] { "gpu core", "gpu total", "core" })
        {
            ISensor? sensor = loadSensors.FirstOrDefault(item =>
                item.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase));
            if (sensor?.Value is float value)
            {
                return value;
            }
        }

        return loadSensors
            .Select(static sensor => sensor.Value)
            .Where(static value => value.HasValue)
            .Select(static value => (double)value!.Value)
            .DefaultIfEmpty(double.NaN)
            .Max() is double fallback && !double.IsNaN(fallback)
                ? fallback
                : null;
    }

    private static ulong? PickMemorySensorBytes(IEnumerable<ISensor> sensors, params string[] nameParts)
    {
        ISensor? sensor = sensors.FirstOrDefault(item =>
            (item.SensorType == SensorType.SmallData || item.SensorType == SensorType.Data) &&
            item.Value is > 0 &&
            nameParts.All(part => item.Name.Contains(part, StringComparison.OrdinalIgnoreCase)) &&
            item.Name.Contains("memory", StringComparison.OrdinalIgnoreCase));

        if (sensor?.Value is not float value || value <= 0)
        {
            return null;
        }

        return (ulong)(value * 1024 * 1024);
    }
}
