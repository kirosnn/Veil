using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal readonly record struct WindowFitNativeSnapshot(
    IntPtr Handle,
    IntPtr RootHandle,
    IntPtr ParentHandle,
    IntPtr OwnerHandle,
    bool IsWindow,
    bool IsVisible,
    bool IsMinimized,
    bool IsMaximized,
    bool IsCloaked,
    int Style,
    int ExtendedStyle,
    Rect WindowRect,
    Rect MonitorRect,
    int ProcessId,
    string ProcessName,
    string ClassName,
    string Title);

internal static class WindowFitCandidateEvaluator
{
    private static readonly string OwnProcessName = Environment.ProcessPath is null
        ? "Veil"
        : Path.GetFileNameWithoutExtension(Environment.ProcessPath);

    private static readonly HashSet<string> PopupClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "#32768",
        "#32770",
        "ComboLBox",
        "tooltips_class32",
        "SysShadow",
        "TaskListThumbnailWnd",
        "MultitaskingViewFrame",
        "Windows.UI.Core.CoreWindow",
        "ApplicationFrameInputSinkWindow"
    };

    internal static bool ShouldFit(WindowFitNativeSnapshot window, WindowFitSettings settings, int ownProcessId)
    {
        if (!settings.Enabled || !window.IsWindow || window.Handle == IntPtr.Zero)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(window.ProcessName)
            || string.IsNullOrWhiteSpace(window.ClassName))
        {
            return false;
        }

        if (window.RootHandle != IntPtr.Zero && window.RootHandle != window.Handle)
        {
            return false;
        }

        if (window.ParentHandle != IntPtr.Zero || window.OwnerHandle != IntPtr.Zero)
        {
            return false;
        }

        if (!window.IsVisible || window.IsMinimized || window.IsCloaked)
        {
            return false;
        }

        uint style = unchecked((uint)window.Style);
        uint extendedStyle = unchecked((uint)window.ExtendedStyle);

        if (Has(style, WS_CHILD) || Has(style, WS_DISABLED) || !Has(style, WS_VISIBLE))
        {
            return false;
        }

        if (Has(extendedStyle, WS_EX_TOOLWINDOW)
            || Has(extendedStyle, WS_EX_NOACTIVATE)
            || Has(extendedStyle, WS_EX_TOPMOST))
        {
            return false;
        }

        if (!Has(style, WS_THICKFRAME) || !Has(style, WS_CAPTION))
        {
            return false;
        }

        if (window.ProcessId == ownProcessId || IsOwnProcessName(window.ProcessName) || IsExcluded(window, settings.ExclusionSet))
        {
            return false;
        }

        if (PopupClassNames.Contains(window.ClassName))
        {
            return false;
        }

        int width = Width(window.WindowRect);
        int height = Height(window.WindowRect);
        if (width < 360 || height < 240)
        {
            return false;
        }

        int monitorWidth = Width(window.MonitorRect);
        int monitorHeight = Height(window.MonitorRect);
        if (!window.IsMaximized && width >= monitorWidth - 4 && height >= monitorHeight - 4)
        {
            return false;
        }

        return true;
    }

    private static bool IsOwnProcessName(string processName)
    {
        return string.Equals(processName, "Veil", StringComparison.OrdinalIgnoreCase)
            || string.Equals(processName, OwnProcessName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcluded(WindowFitNativeSnapshot window, IReadOnlySet<string> exclusions)
        => exclusions.Contains(window.ProcessName)
            || exclusions.Contains(window.ClassName)
            || exclusions.Contains(window.Title);

    private static bool Has(uint value, int flag)
        => (value & unchecked((uint)flag)) != 0;

    private static int Width(Rect rect) => Math.Max(0, rect.Right - rect.Left);

    private static int Height(Rect rect) => Math.Max(0, rect.Bottom - rect.Top);
}
