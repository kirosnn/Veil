using System.Runtime.InteropServices;

namespace Veil.Services;

internal static partial class GraphicsMemoryMonitor
{
    private const uint DxgiCreateFactoryFlags = 0;
    private const uint DXGI_ERROR_NOT_FOUND = 0x887A0002;
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 0x2;
    private const ulong MinimumMeaningfulSegmentBudgetBytes = 256UL * 1024 * 1024;

    internal sealed record GpuInfo(string Name, ulong UsedBytes, ulong TotalBytes, double UsagePercent);

    internal static List<GpuInfo> GetAllGpuInfo()
    {
        var results = new List<GpuInfo>();
        IntPtr factoryPtr = IntPtr.Zero;
        try
        {
            Guid factoryGuid = typeof(IDXGIFactory1).GUID;
            int hr = CreateDXGIFactory1(in factoryGuid, out factoryPtr);
            if (hr < 0 || factoryPtr == IntPtr.Zero)
            {
                return results;
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

                    string name = desc.Description?.Trim('\0').Trim() ?? "GPU";
                    if (TryGetAdapterMemorySnapshot(adapter, desc, out AdapterMemorySnapshot snapshot))
                    {
                        double percent = snapshot.TotalBytes > 0
                            ? (double)snapshot.UsedBytes / snapshot.TotalBytes * 100
                            : 0;
                        results.Add(new GpuInfo(name, snapshot.UsedBytes, snapshot.TotalBytes, Math.Clamp(percent, 0, 100)));
                    }
                    else
                    {
                        results.Add(new GpuInfo(name, 0, 0, 0));
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
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero)
            {
                Marshal.Release(factoryPtr);
            }
        }

        return results;
    }

    internal static bool TryGetFreeMemoryRatio(out double freeRatio)
    {
        freeRatio = 1.0;

        IntPtr factoryPtr = IntPtr.Zero;
        try
        {
            Guid factoryGuid = typeof(IDXGIFactory1).GUID;
            int hr = CreateDXGIFactory1(in factoryGuid, out factoryPtr);
            if (hr < 0 || factoryPtr == IntPtr.Zero)
            {
                return false;
            }

            var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPtr);
            double worstRatio = double.PositiveInfinity;
            bool foundRatio = false;

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

                    if (!TryGetAdapterMemorySnapshot(adapter, desc, out AdapterMemorySnapshot snapshot))
                    {
                        continue;
                    }

                    if (snapshot.TotalBytes < MinimumMeaningfulSegmentBudgetBytes && snapshot.UsedBytes == 0)
                    {
                        continue;
                    }

                    worstRatio = Math.Min(worstRatio, snapshot.FreeRatio);
                    foundRatio = true;
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }

            if (!foundRatio)
            {
                return false;
            }

            freeRatio = Math.Clamp(worstRatio, 0.0, 1.0);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero)
            {
                Marshal.Release(factoryPtr);
            }
        }
    }

    private static bool TryGetAdapterMemorySnapshot(IDXGIAdapter1 adapter, DXGI_ADAPTER_DESC1 desc, out AdapterMemorySnapshot snapshot)
    {
        ulong totalBudget = 0;
        ulong totalUsage = 0;
        double worstFreeRatio = double.PositiveInfinity;
        bool hasSegmentRatio = false;

        if (adapter is IDXGIAdapter3 adapter3)
        {
            foreach (DXGI_MEMORY_SEGMENT_GROUP segmentGroup in Enum.GetValues<DXGI_MEMORY_SEGMENT_GROUP>())
            {
                int hr = adapter3.QueryVideoMemoryInfo(0, segmentGroup, out DXGI_QUERY_VIDEO_MEMORY_INFO info);
                if (hr < 0 || info.Budget == 0)
                {
                    continue;
                }

                totalBudget += info.Budget;
                totalUsage += info.CurrentUsage;

                if (info.Budget < MinimumMeaningfulSegmentBudgetBytes)
                {
                    continue;
                }

                ulong freeBytes = info.Budget > info.CurrentUsage
                    ? info.Budget - info.CurrentUsage
                    : 0;
                double ratio = (double)freeBytes / info.Budget;
                worstFreeRatio = Math.Min(worstFreeRatio, ratio);
                hasSegmentRatio = true;
            }
        }

        if (totalBudget == 0)
        {
            ulong dedicated = (ulong)(nuint)desc.DedicatedVideoMemory;
            ulong shared = (ulong)(nuint)desc.SharedSystemMemory;
            totalBudget = dedicated + shared;
        }

        if (totalBudget == 0)
        {
            snapshot = default;
            return false;
        }

        if (!hasSegmentRatio)
        {
            ulong freeBytes = totalBudget > totalUsage
                ? totalBudget - totalUsage
                : 0;
            worstFreeRatio = (double)freeBytes / totalBudget;
        }

        snapshot = new AdapterMemorySnapshot(
            totalUsage,
            totalBudget,
            Math.Clamp(worstFreeRatio, 0.0, 1.0));
        return true;
    }

    private readonly record struct AdapterMemorySnapshot(ulong UsedBytes, ulong TotalBytes, double FreeRatio);

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
