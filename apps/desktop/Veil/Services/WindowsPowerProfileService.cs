using System.Runtime.InteropServices;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed class WindowsPowerProfileService : IDisposable
{
    private static readonly Guid BalancedScheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformanceScheme = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid PowerSaverScheme = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid UltimatePerformanceScheme = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private static readonly Lazy<bool> _isLaptopDevice = new(DetectLaptopDevice);
    private static readonly Lazy<PowerHardwareProfile> _hardwareProfile = new(DetectPowerHardwareProfile);

    private Guid? _originalSchemeGuid;
    private Guid? _managedSchemeGuid;
    private ManagedPowerMode _managedMode;

    internal bool IsManagingBoost => _managedMode == ManagedPowerMode.Boost && _originalSchemeGuid.HasValue && _managedSchemeGuid.HasValue;

    internal bool IsLaptopDevice => _isLaptopDevice.Value;

    internal bool IsOnAcPowerNow => IsOnAcPower();

    internal bool TryBoostPerformanceProfile()
    {
        if (!IsOnAcPower())
        {
            RestoreOriginalProfile();
            return false;
        }

        if (!TryGetActiveScheme(out Guid activeScheme))
        {
            return false;
        }

        SyncManagedState(activeScheme);

        if (activeScheme == UltimatePerformanceScheme || activeScheme == HighPerformanceScheme)
        {
            return false;
        }

        return TryApplyManagedScheme(activeScheme, UltimatePerformanceScheme, HighPerformanceScheme, ManagedPowerMode.Boost);
    }

    internal bool TryApplyQuietProfile()
    {
        if (!IsLaptopDevice)
        {
            RestoreOriginalProfile();
            return false;
        }

        if (!TryGetActiveScheme(out Guid activeScheme))
        {
            return false;
        }

        SyncManagedState(activeScheme);

        Guid preferredSchemeGuid = ResolveQuietSchemeGuid(IsOnAcPower(), _hardwareProfile.Value);
        Guid? fallbackSchemeGuid = preferredSchemeGuid == BalancedScheme
            ? null
            : BalancedScheme;

        if (activeScheme == preferredSchemeGuid ||
            (fallbackSchemeGuid.HasValue && activeScheme == fallbackSchemeGuid.Value))
        {
            return false;
        }

        return TryApplyManagedScheme(activeScheme, preferredSchemeGuid, fallbackSchemeGuid, ManagedPowerMode.QuietBackground);
    }

    internal void RestoreOriginalProfile()
    {
        if (_originalSchemeGuid is not Guid originalSchemeGuid || _managedSchemeGuid is not Guid managedSchemeGuid)
        {
            return;
        }

        try
        {
            if (!TryGetActiveScheme(out Guid activeScheme) || activeScheme == managedSchemeGuid)
            {
                TrySetActiveScheme(originalSchemeGuid);
            }
        }
        finally
        {
            _originalSchemeGuid = null;
            _managedSchemeGuid = null;
            _managedMode = ManagedPowerMode.None;
        }
    }

    public void Dispose()
    {
        RestoreOriginalProfile();
    }

    internal static Guid ResolveQuietSchemeGuid(bool onAcPower)
    {
        return PowerSaverScheme;
    }

    internal static Guid ResolveQuietSchemeGuid(bool onAcPower, int logicalProcessorCount, double totalMemoryGb)
    {
        return ResolveQuietSchemeGuid(onAcPower, new PowerHardwareProfile(logicalProcessorCount, totalMemoryGb));
    }

    internal static bool IsPortablePowerManagedDevice(bool lidPresent, bool systemBatteriesPresent)
    {
        return lidPresent || systemBatteriesPresent;
    }

    private static Guid ResolveQuietSchemeGuid(bool onAcPower, PowerHardwareProfile hardwareProfile)
    {
        if (onAcPower)
        {
            return PowerSaverScheme;
        }

        bool constrainedCpu = hardwareProfile.LogicalProcessorCount <= 6;
        bool constrainedMemory = hardwareProfile.TotalMemoryGb < 12;

        return constrainedCpu && constrainedMemory
            ? PowerSaverScheme
            : BalancedScheme;
    }

    private bool TryApplyManagedScheme(Guid activeScheme, Guid primarySchemeGuid, Guid? fallbackSchemeGuid, ManagedPowerMode mode)
    {
        if (_originalSchemeGuid is null)
        {
            _originalSchemeGuid = activeScheme;
        }

        if (TrySetActiveScheme(primarySchemeGuid))
        {
            _managedSchemeGuid = primarySchemeGuid;
            _managedMode = mode;
            return true;
        }

        if (fallbackSchemeGuid is Guid fallbackGuid && activeScheme != fallbackGuid && TrySetActiveScheme(fallbackGuid))
        {
            _managedSchemeGuid = fallbackGuid;
            _managedMode = mode;
            return true;
        }

        if (_originalSchemeGuid == activeScheme)
        {
            _originalSchemeGuid = null;
        }

        _managedSchemeGuid = null;
        _managedMode = ManagedPowerMode.None;
        return false;
    }

    private void SyncManagedState(Guid activeScheme)
    {
        if (_managedSchemeGuid.HasValue && activeScheme != _managedSchemeGuid.Value)
        {
            _originalSchemeGuid = null;
            _managedSchemeGuid = null;
            _managedMode = ManagedPowerMode.None;
        }
    }

    private static bool DetectLaptopDevice()
    {
        return TryGetPowerCapabilities(out SystemPowerCapabilities capabilities) &&
            IsPortablePowerManagedDevice(capabilities.LidPresent, capabilities.SystemBatteriesPresent);
    }

    private static PowerHardwareProfile DetectPowerHardwareProfile()
    {
        double totalMemoryGb = 16;
        MemoryStatusEx memoryStatus = MemoryStatusEx.Create();
        if (GlobalMemoryStatusEx(ref memoryStatus) && memoryStatus.ullTotalPhys > 0)
        {
            totalMemoryGb = memoryStatus.ullTotalPhys / (1024.0 * 1024 * 1024);
        }

        return new PowerHardwareProfile(Environment.ProcessorCount, totalMemoryGb);
    }

    private static bool IsOnAcPower()
    {
        return GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus)
            && systemPowerStatus.ACLineStatus == 1;
    }

    private static bool TryGetPowerCapabilities(out SystemPowerCapabilities capabilities)
    {
        try
        {
            return GetPwrCapabilities(out capabilities);
        }
        catch
        {
            capabilities = default;
            return false;
        }
    }

    private static bool TryGetActiveScheme(out Guid schemeGuid)
    {
        IntPtr schemePointer = IntPtr.Zero;

        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out schemePointer) != 0 || schemePointer == IntPtr.Zero)
            {
                schemeGuid = Guid.Empty;
                return false;
            }

            schemeGuid = Marshal.PtrToStructure<Guid>(schemePointer);
            return true;
        }
        catch
        {
            schemeGuid = Guid.Empty;
            return false;
        }
        finally
        {
            if (schemePointer != IntPtr.Zero)
            {
                _ = LocalFree(schemePointer);
            }
        }
    }

    private static bool TrySetActiveScheme(Guid schemeGuid)
    {
        try
        {
            return PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid) == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPwrCapabilities(out SystemPowerCapabilities systemPowerCapabilities);

    [DllImport("powrprof.dll", EntryPoint = "PowerGetActiveScheme")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll", EntryPoint = "PowerSetActiveScheme")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    private enum ManagedPowerMode
    {
        None,
        Boost,
        QuietBackground
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryReportingScale
    {
        public uint Granularity;
        public uint Capacity;
    }

    private readonly record struct PowerHardwareProfile(int LogicalProcessorCount, double TotalMemoryGb);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerCapabilities
    {
        [MarshalAs(UnmanagedType.U1)] public bool PowerButtonPresent;
        [MarshalAs(UnmanagedType.U1)] public bool SleepButtonPresent;
        [MarshalAs(UnmanagedType.U1)] public bool LidPresent;
        public int SystemS1;
        public int SystemS2;
        public int SystemS3;
        public int SystemS4;
        public int SystemS5;
        [MarshalAs(UnmanagedType.U1)] public bool HiberFilePresent;
        [MarshalAs(UnmanagedType.U1)] public bool FullWake;
        [MarshalAs(UnmanagedType.U1)] public bool VideoDimPresent;
        [MarshalAs(UnmanagedType.U1)] public bool ApmPresent;
        [MarshalAs(UnmanagedType.U1)] public bool UpsPresent;
        [MarshalAs(UnmanagedType.U1)] public bool ThermalControl;
        [MarshalAs(UnmanagedType.U1)] public bool ProcessorThrottle;
        public byte ProcessorMinThrottle;
        public byte ProcessorMaxThrottle;
        [MarshalAs(UnmanagedType.U1)] public bool FastSystemS4;
        [MarshalAs(UnmanagedType.U1)] public bool Hiberboot;
        [MarshalAs(UnmanagedType.U1)] public bool WakeAlarmPresent;
        [MarshalAs(UnmanagedType.U1)] public bool AoAc;
        [MarshalAs(UnmanagedType.U1)] public bool DiskSpinDown;
        public byte HiberFileType;
        [MarshalAs(UnmanagedType.U1)] public bool AoAcConnectivitySupported;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Spare3;
        [MarshalAs(UnmanagedType.U1)] public bool SystemBatteriesPresent;
        [MarshalAs(UnmanagedType.U1)] public bool BatteriesAreShortTerm;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public BatteryReportingScale[] BatteryScale;
        public int AcOnLineWake;
        public int SoftLidWake;
        public int RtcWake;
        public int MinDeviceWakeState;
        public int DefaultLowLatencyWake;
    }
}
