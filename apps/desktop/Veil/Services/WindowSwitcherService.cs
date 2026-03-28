using System.Diagnostics;
using System.Globalization;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed record WindowSwitchEntry(
    IntPtr Handle,
    string AppName,
    string WindowTitle,
    bool IsMinimized,
    global::Windows.UI.Color AccentColor)
{
    internal string DisplayTitle => string.Equals(AppName, WindowTitle, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(WindowTitle)
        ? AppName
        : $"{AppName} - {WindowTitle}";
}

internal static class WindowSwitcherService
{
    private static readonly HashSet<string> ExcludedWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd"
    };

    internal static IReadOnlyList<WindowSwitchEntry> GetSwitchableWindows()
    {
        IntPtr foregroundWindow = GetForegroundWindow();
        WindowSwitchEntry? foregroundEntry = null;
        var entries = new List<WindowSwitchEntry>();
        var seenHandles = new HashSet<IntPtr>();

        EnumWindows((hwnd, _) =>
        {
            if (!TryCreateEntry(hwnd, out WindowSwitchEntry entry))
            {
                return true;
            }

            if (!seenHandles.Add(entry.Handle))
            {
                return true;
            }

            if (entry.Handle == foregroundWindow)
            {
                foregroundEntry = entry;
            }
            else
            {
                entries.Add(entry);
            }

            return true;
        }, IntPtr.Zero);

        if (foregroundEntry is not null)
        {
            entries.Insert(0, foregroundEntry);
        }

        return entries;
    }

    internal static void ActivateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (IsIconic(hwnd))
        {
            ShowWindowNative(hwnd, SW_RESTORE);
        }

        SetForegroundWindow(hwnd);
    }

    internal static ScreenBounds GetDisplayBoundsForCursorOrPrimary()
    {
        List<MonitorInfo2> monitors = MonitorService.GetAllMonitors();
        if (monitors.Count == 0)
        {
            return new ScreenBounds(0, 0, 1280, 720);
        }

        MonitorInfo2 primaryMonitor = monitors.FirstOrDefault(static monitor => monitor.IsPrimary) ?? monitors[0];
        if (!GetCursorPos(out Point cursor))
        {
            return new ScreenBounds(
                primaryMonitor.WorkArea.Left,
                primaryMonitor.WorkArea.Top,
                primaryMonitor.WorkArea.Right,
                primaryMonitor.WorkArea.Bottom);
        }

        MonitorInfo2 bestMonitor = monitors.FirstOrDefault(monitor =>
            cursor.X >= monitor.Bounds.Left &&
            cursor.X < monitor.Bounds.Right &&
            cursor.Y >= monitor.Bounds.Top &&
            cursor.Y < monitor.Bounds.Bottom)
            ?? primaryMonitor;

        return new ScreenBounds(
            bestMonitor.WorkArea.Left,
            bestMonitor.WorkArea.Top,
            bestMonitor.WorkArea.Right,
            bestMonitor.WorkArea.Bottom);
    }

    private static bool TryCreateEntry(IntPtr hwnd, out WindowSwitchEntry entry)
    {
        entry = default!;

        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return false;
        }

        string className = GetClassName(hwnd);
        if (ExcludedWindowClasses.Contains(className))
        {
            return false;
        }

        int exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
        {
            return false;
        }

        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out uint cloaked, sizeof(uint)) == 0 && cloaked != 0)
        {
            return false;
        }

        bool isMinimized = IsIconic(hwnd);
        if (!IsWindowVisible(hwnd) && !isMinimized)
        {
            return false;
        }

        string windowTitle = GetWindowTitle(hwnd);
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return false;
        }

        string appName = GetProcessLabel((int)processId);
        entry = new WindowSwitchEntry(
            hwnd,
            appName,
            windowTitle.Trim(),
            isMinimized,
            GetWindowAccentColor(hwnd));
        return true;
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var classNameBuffer = new char[256];
        int classNameLength = GetClassNameW(hwnd, classNameBuffer, classNameBuffer.Length);
        return classNameLength <= 0
            ? string.Empty
            : new string(classNameBuffer, 0, classNameLength);
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var titleBuffer = new char[512];
        int titleLength = GetWindowTextW(hwnd, titleBuffer, titleBuffer.Length);
        return titleLength <= 0
            ? string.Empty
            : new string(titleBuffer, 0, titleLength);
    }

    private static string GetProcessLabel(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            string? productName = process.MainModule?.FileVersionInfo.ProductName;
            if (!string.IsNullOrWhiteSpace(productName))
            {
                return productName.Trim();
            }

            string? fileDescription = process.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(fileDescription))
            {
                return fileDescription.Trim();
            }

            if (string.IsNullOrWhiteSpace(process.ProcessName))
            {
                return "App";
            }

            string normalizedName = process.ProcessName.Replace('_', ' ').Trim();
            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(normalizedName);
        }
        catch
        {
            return "App";
        }
    }

    private static global::Windows.UI.Color GetWindowAccentColor(IntPtr hwnd)
    {
        global::Windows.UI.Color? captionColor = TryGetDwmCaptionColor(hwnd);
        if (captionColor.HasValue)
        {
            return captionColor.Value;
        }

        global::Windows.UI.Color? colorizationColor = TryGetDwmColorizationColor();
        if (colorizationColor.HasValue)
        {
            return colorizationColor.Value;
        }

        return global::Windows.UI.Color.FromArgb(255, 22, 22, 28);
    }

    private static global::Windows.UI.Color? TryGetDwmCaptionColor(IntPtr hwnd)
    {
        int hr = DwmGetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, out uint captionColor, sizeof(uint));
        if (hr != 0 || captionColor == DWMWA_COLOR_DEFAULT || captionColor == DWMWA_COLOR_NONE)
        {
            return null;
        }

        byte r = (byte)(captionColor & 0xFF);
        byte g = (byte)((captionColor >> 8) & 0xFF);
        byte b = (byte)((captionColor >> 16) & 0xFF);
        return global::Windows.UI.Color.FromArgb(255, r, g, b);
    }

    private static global::Windows.UI.Color? TryGetDwmColorizationColor()
    {
        int hr = DwmGetColorizationColor(out uint colorizationColor, out _);
        if (hr != 0)
        {
            return null;
        }

        byte a = (byte)((colorizationColor >> 24) & 0xFF);
        byte r = (byte)((colorizationColor >> 16) & 0xFF);
        byte g = (byte)((colorizationColor >> 8) & 0xFF);
        byte b = (byte)(colorizationColor & 0xFF);

        if (a == 0)
        {
            return null;
        }

        return global::Windows.UI.Color.FromArgb(255, r, g, b);
    }
}
