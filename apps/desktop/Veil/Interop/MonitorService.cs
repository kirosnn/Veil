using System.Runtime.InteropServices;
using static Veil.Interop.NativeMethods;

namespace Veil.Interop;

internal readonly record struct ScreenBounds(int Left, int Top, int Right, int Bottom);

internal sealed class MonitorInfo2
{
    public required IntPtr Handle { get; init; }
    public required Rect Bounds { get; init; }
    public required Rect WorkArea { get; init; }
    public required bool IsPrimary { get; init; }
    public required string DeviceName { get; init; }
    public string Id => DeviceName;

    public ScreenBounds ToScreenBounds() => new(Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom);
}

internal static class MonitorService
{
    internal static List<MonitorInfo2> GetAllMonitors()
    {
        var monitors = new List<MonitorInfo2>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref Rect _, IntPtr _) =>
        {
            var info = MonitorInfoEx.Create();
            if (GetMonitorInfoExW(hMonitor, ref info))
            {
                monitors.Add(new MonitorInfo2
                {
                    Handle = hMonitor,
                    Bounds = info.Monitor,
                    WorkArea = info.WorkArea,
                    IsPrimary = (info.Flags & 1) != 0,
                    DeviceName = string.IsNullOrWhiteSpace(info.DeviceName)
                        ? $"monitor-{monitors.Count}"
                        : info.DeviceName
                });
            }
            return true;
        }, IntPtr.Zero);

        return monitors
            .OrderByDescending(static monitor => monitor.IsPrimary)
            .ThenBy(static monitor => monitor.Bounds.Left)
            .ThenBy(static monitor => monitor.Bounds.Top)
            .ToList();
    }
}
