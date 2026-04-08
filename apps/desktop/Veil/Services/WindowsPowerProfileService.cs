using System.Runtime.InteropServices;

namespace Veil.Services;

internal sealed class WindowsPowerProfileService : IDisposable
{
    private static readonly Guid HighPerformanceScheme = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid UltimatePerformanceScheme = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    private Guid? _originalSchemeGuid;
    private Guid? _boostSchemeGuid;

    internal bool IsManagingBoost => _originalSchemeGuid.HasValue && _boostSchemeGuid.HasValue;

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

        if (activeScheme == UltimatePerformanceScheme || activeScheme == HighPerformanceScheme)
        {
            return false;
        }

        if (_originalSchemeGuid is null)
        {
            _originalSchemeGuid = activeScheme;
        }

        if (TrySetActiveScheme(UltimatePerformanceScheme))
        {
            _boostSchemeGuid = UltimatePerformanceScheme;
            return true;
        }

        if (TrySetActiveScheme(HighPerformanceScheme))
        {
            _boostSchemeGuid = HighPerformanceScheme;
            return true;
        }

        if (_originalSchemeGuid == activeScheme)
        {
            _originalSchemeGuid = null;
        }

        return false;
    }

    internal void RestoreOriginalProfile()
    {
        if (_originalSchemeGuid is not Guid originalSchemeGuid || _boostSchemeGuid is not Guid boostSchemeGuid)
        {
            return;
        }

        try
        {
            if (!TryGetActiveScheme(out Guid activeScheme) || activeScheme == boostSchemeGuid)
            {
                TrySetActiveScheme(originalSchemeGuid);
            }
        }
        finally
        {
            _originalSchemeGuid = null;
            _boostSchemeGuid = null;
        }
    }

    public void Dispose()
    {
        RestoreOriginalProfile();
    }

    private static bool IsOnAcPower()
    {
        return GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus)
            && systemPowerStatus.ACLineStatus == 1;
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

    [DllImport("powrprof.dll", EntryPoint = "PowerGetActiveScheme")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll", EntryPoint = "PowerSetActiveScheme")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

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
}
