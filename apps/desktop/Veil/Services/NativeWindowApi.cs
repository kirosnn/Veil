using System.Diagnostics;
using System.Runtime.InteropServices;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed class NativeWindowApi
{
    private static readonly TimeSpan ProcessNameCacheDuration = TimeSpan.FromSeconds(10);
    private readonly object _processNameCacheGate = new();
    private readonly Dictionary<int, CachedProcessInfo> _processNameCache = [];

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
            GetProcessPath(processId),
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
        return GetCachedProcessInfo(processId).Name;
    }

    private string GetProcessPath(uint processId)
    {
        return GetCachedProcessInfo(processId).Path;
    }

    private CachedProcessInfo GetCachedProcessInfo(uint processId)
    {
        if (processId == 0)
        {
            return new CachedProcessInfo(string.Empty, string.Empty, DateTime.UtcNow);
        }

        int id = unchecked((int)processId);
        DateTime now = DateTime.UtcNow;
        lock (_processNameCacheGate)
        {
            if (_processNameCache.TryGetValue(id, out CachedProcessInfo cached)
                && now - cached.CachedAtUtc <= ProcessNameCacheDuration)
            {
                return cached;
            }
        }

        CachedProcessInfo processInfo = ReadProcessInfo(id, now);

        lock (_processNameCacheGate)
        {
            _processNameCache[id] = processInfo;
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

        return processInfo;
    }

    private static CachedProcessInfo ReadProcessInfo(int processId, DateTime cachedAtUtc)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            string path;
            try
            {
                path = process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                path = string.Empty;
            }

            return new CachedProcessInfo(process.ProcessName, path, cachedAtUtc);
        }
        catch
        {
            return new CachedProcessInfo(string.Empty, string.Empty, cachedAtUtc);
        }
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        int result = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out uint cloaked, Marshal.SizeOf<uint>());
        return result == 0 && cloaked != 0;
    }
}

internal readonly record struct CachedProcessInfo(string Name, string Path, DateTime CachedAtUtc);

internal readonly record struct MonitorWindowFitLayout(
    IntPtr Handle,
    string DeviceName,
    Rect Monitor,
    Rect WorkArea);
