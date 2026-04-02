using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Services;

internal sealed class WindowsDesktopShellBridge : IDesktopShellBridge
{
    private const int ToggleDesktopIconsCommandId = 0x7402;

    public DesktopShellState CaptureState()
        => new(
            DesktopIconsHidden: AreDesktopIconsHidden(),
            TaskbarsHidden: AreTaskbarsHidden(),
            StartLayoutFile: new RegistryStringValueSnapshot(false, null),
            LockedStartLayout: new RegistryDwordValueSnapshot(false, 0),
            RecycleBinNewStartPanel: new RegistryDwordValueSnapshot(false, 0),
            RecycleBinClassicStartMenu: new RegistryDwordValueSnapshot(false, 0));

    internal static bool AreDesktopIconsHidden()
    {
        IntPtr desktopListView = FindDesktopListView();
        if (desktopListView == IntPtr.Zero)
        {
            return false;
        }

        return !IsWindowVisible(desktopListView);
    }

    public DesktopTaskbarArtifacts PrepareTaskbarArtifacts()
        => new(
            ShortcutName: "Corbeille",
            ShortcutPath: string.Empty,
            ShortcutBackupPath: null,
            LauncherScriptPath: string.Empty,
            TaskbarPolicyPath: string.Empty);

    public void ApplyTaskbarPolicy(DesktopTaskbarArtifacts artifacts)
    {
        SetTaskbarsHidden(true);
    }

    public void SetDesktopIconsHidden(bool hidden)
    {
        bool currentlyHidden = AreDesktopIconsHidden();
        if (currentlyHidden == hidden)
        {
            return;
        }

        IntPtr desktopDefView = FindDesktopDefView();
        if (desktopDefView == IntPtr.Zero)
        {
            return;
        }

        PostMessageW(desktopDefView, WM_COMMAND, (IntPtr)ToggleDesktopIconsCommandId, IntPtr.Zero);
    }

    public void SetRecycleBinDesktopIconHidden(bool hidden)
    {
    }

    public void RestartExplorer()
    {
    }

    public bool IsTaskbarShortcutPinned(string shortcutName)
        => true;

    public void RestoreState(DesktopShellState state)
    {
        SetDesktopIconsHidden(state.DesktopIconsHidden);
        SetTaskbarsHidden(state.TaskbarsHidden);
    }

    public void CleanupArtifacts(DesktopTaskbarArtifacts artifacts)
    {
    }

    private static IntPtr FindDesktopDefView()
    {
        IntPtr shellWindow = GetShellWindow();
        if (shellWindow != IntPtr.Zero)
        {
            IntPtr defView = FindWindowExW(shellWindow, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                return defView;
            }
        }

        IntPtr desktopDefView = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            IntPtr defView = FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero)
            {
                return true;
            }

            desktopDefView = defView;
            return false;
        }, IntPtr.Zero);

        return desktopDefView;
    }

    private static IntPtr FindDesktopListView()
    {
        IntPtr defView = FindDesktopDefView();
        if (defView == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return FindWindowExW(defView, IntPtr.Zero, "SysListView32", null);
    }

    private static bool AreTaskbarsHidden()
    {
        foreach (IntPtr hwnd in EnumerateTaskbarWindows())
        {
            if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd))
            {
                return false;
            }
        }

        return true;
    }

    private static void SetTaskbarsHidden(bool hidden)
    {
        int command = hidden ? SW_HIDE : SW_SHOW;
        foreach (IntPtr hwnd in EnumerateTaskbarWindows())
        {
            if (hwnd != IntPtr.Zero)
            {
                ShowWindowNative(hwnd, command);
            }
        }
    }

    private static IReadOnlyList<IntPtr> EnumerateTaskbarWindows()
    {
        var handles = new List<IntPtr>();
        IntPtr primaryTaskbar = FindWindowW("Shell_TrayWnd", null);
        if (primaryTaskbar != IntPtr.Zero)
        {
            handles.Add(primaryTaskbar);
        }

        EnumWindows((hwnd, _) =>
        {
            var classNameBuffer = new char[256];
            int classNameLength = GetClassNameW(hwnd, classNameBuffer, classNameBuffer.Length);
            if (classNameLength <= 0)
            {
                return true;
            }

            string className = new(classNameBuffer, 0, classNameLength);
            if (string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.Ordinal))
            {
                handles.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);

        return handles;
    }
}
