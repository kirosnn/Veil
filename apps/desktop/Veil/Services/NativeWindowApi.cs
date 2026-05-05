using System.Diagnostics;
using System.Runtime.InteropServices;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed class NativeWindowApi
{
    private static readonly TimeSpan ProcessNameCacheDuration = TimeSpan.FromSeconds(10);
    private readonly object _processNameCacheGate = new();
    private readonly Dictionary<int, CachedProcessName> _processNameCache = [];

    internal bool TryCreateSnapshot(IntPtr hwnd, out WindowFitNativeSnapshot snapshot)
    {
        snapshot = default;

        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return false;
        }

        if (!GetWindowRect(hwnd, out Rect windowRect))
        {
            return false;
        }

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var monitorInfo = MonitorInfo.Create();
        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref monitorInfo))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out uint processId);

        snapshot = new WindowFitNativeSnapshot(
            hwnd,
            GetAncestor(hwnd, GA_ROOT),
            GetParent(hwnd),
            GetWindow(hwnd, GW_OWNER),
            true,
            IsWindowVisible(hwnd),
            IsIconic(hwnd),
            IsZoomed(hwnd),
            IsCloaked(hwnd),
            GetWindowLongW(hwnd, GWL_STYLE),
            GetWindowLongW(hwnd, GWL_EXSTYLE),
            windowRect,
            monitorInfo.Monitor,
            unchecked((int)processId),
            GetProcessName(processId),
            GetClassName(hwnd),
            GetWindowTitle(hwnd));

        return true;
    }

    internal bool TryGetMonitorLayout(IntPtr hwnd, out MonitorWindowFitLayout layout)
    {
        layout = default;
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            monitor = MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTONEAREST);
        }

        var info = MonitorInfoEx.Create();
        if (monitor == IntPtr.Zero || !GetMonitorInfoExW(monitor, ref info))
        {
            return false;
        }

        layout = new MonitorWindowFitLayout(monitor, info.DeviceName, info.Monitor, info.WorkArea);
        return true;
    }

    internal bool SetWindowBounds(IntPtr hwnd, Rect bounds)
        => SetWindowPos(
            hwnd,
            IntPtr.Zero,
            bounds.Left,
            bounds.Top,
            bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top,
            SWP_NOZORDER | SWP_NOACTIVATE);

    internal bool RestoreWindow(IntPtr hwnd)
        => ShowWindowNative(hwnd, SW_RESTORE);

    internal static string GetClassName(IntPtr hwnd)
    {
        char[] buffer = new char[256];
        int length = GetClassNameW(hwnd, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    internal static string GetWindowTitle(IntPtr hwnd)
    {
        char[] buffer = new char[512];
        int length = GetWindowTextW(hwnd, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    private string GetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return string.Empty;
        }

        int id = unchecked((int)processId);
        DateTime now = DateTime.UtcNow;
        lock (_processNameCacheGate)
        {
            if (_processNameCache.TryGetValue(id, out CachedProcessName cached)
                && now - cached.CachedAtUtc <= ProcessNameCacheDuration)
            {
                return cached.Name;
            }
        }

        string processName;
        try
        {
            using Process process = Process.GetProcessById(id);
            processName = process.ProcessName;
        }
        catch
        {
            processName = string.Empty;
        }

        lock (_processNameCacheGate)
        {
            _processNameCache[id] = new CachedProcessName(processName, now);
            if (_processNameCache.Count > 256)
            {
                foreach (int staleProcessId in _processNameCache
                    .Where(entry => now - entry.Value.CachedAtUtc > ProcessNameCacheDuration)
                    .Select(static entry => entry.Key)
                    .ToArray())
                {
                    _processNameCache.Remove(staleProcessId);
                }
            }
        }

        return processName;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        int result = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out uint cloaked, Marshal.SizeOf<uint>());
        return result == 0 && cloaked != 0;
    }
}

internal readonly record struct CachedProcessName(string Name, DateTime CachedAtUtc);

internal readonly record struct MonitorWindowFitLayout(
    IntPtr Handle,
    string DeviceName,
    Rect Monitor,
    Rect WorkArea);
