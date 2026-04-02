using Veil.Services;

namespace Veil.Tests;

[TestClass]
public sealed class DesktopShellServiceTests
{
    [TestMethod]
    public void TryApplyLaunchState_applies_shell_changes_when_taskbar_pin_is_detected()
    {
        var bridge = new FakeDesktopShellBridge
        {
            IsTaskbarShortcutPinnedResult = true
        };
        var service = new DesktopShellService(bridge);

        bool applied = service.TryApplyLaunchState();

        Assert.IsTrue(applied);
        CollectionAssert.AreEqual(
            new[]
            {
                "CaptureState",
                "PrepareTaskbarArtifacts",
                "ApplyTaskbarPolicy:Corbeille",
                "SetDesktopIconsHidden:True",
                "SetRecycleBinDesktopIconHidden:True",
                "RestartExplorer",
                "IsTaskbarShortcutPinned:Corbeille"
            },
            bridge.Calls);
    }

    [TestMethod]
    public void TryApplyLaunchState_rolls_back_when_taskbar_pin_cannot_be_verified()
    {
        var bridge = new FakeDesktopShellBridge
        {
            IsTaskbarShortcutPinnedResult = false
        };
        var service = new DesktopShellService(bridge);

        bool applied = service.TryApplyLaunchState();

        Assert.IsFalse(applied);
        CollectionAssert.AreEqual(
            new[]
            {
                "CaptureState",
                "PrepareTaskbarArtifacts",
                "ApplyTaskbarPolicy:Corbeille",
                "SetDesktopIconsHidden:True",
                "SetRecycleBinDesktopIconHidden:True",
                "RestartExplorer",
                "IsTaskbarShortcutPinned:Corbeille",
                "RestoreState",
                "RestartExplorer",
                "CleanupArtifacts:Corbeille"
            },
            bridge.Calls);
    }

    private sealed class FakeDesktopShellBridge : IDesktopShellBridge
    {
        public List<string> Calls { get; } = [];

        public bool IsTaskbarShortcutPinnedResult { get; init; }

        public DesktopShellState CaptureState()
        {
            Calls.Add("CaptureState");
            return new DesktopShellState(
                DesktopIconsHidden: false,
                TaskbarsHidden: false,
                StartLayoutFile: new RegistryStringValueSnapshot(false, null),
                LockedStartLayout: new RegistryDwordValueSnapshot(false, 0),
                RecycleBinNewStartPanel: new RegistryDwordValueSnapshot(false, 0),
                RecycleBinClassicStartMenu: new RegistryDwordValueSnapshot(false, 0));
        }

        public DesktopTaskbarArtifacts PrepareTaskbarArtifacts()
        {
            Calls.Add("PrepareTaskbarArtifacts");
            return new DesktopTaskbarArtifacts(
                ShortcutName: "Corbeille",
                ShortcutPath: "shortcut",
                ShortcutBackupPath: null,
                LauncherScriptPath: "script",
                TaskbarPolicyPath: "policy");
        }

        public void ApplyTaskbarPolicy(DesktopTaskbarArtifacts artifacts)
        {
            Calls.Add($"ApplyTaskbarPolicy:{artifacts.ShortcutName}");
        }

        public void SetDesktopIconsHidden(bool hidden)
        {
            Calls.Add($"SetDesktopIconsHidden:{hidden}");
        }

        public void SetRecycleBinDesktopIconHidden(bool hidden)
        {
            Calls.Add($"SetRecycleBinDesktopIconHidden:{hidden}");
        }

        public void RestartExplorer()
        {
            Calls.Add("RestartExplorer");
        }

        public bool IsTaskbarShortcutPinned(string shortcutName)
        {
            Calls.Add($"IsTaskbarShortcutPinned:{shortcutName}");
            return IsTaskbarShortcutPinnedResult;
        }

        public void RestoreState(DesktopShellState state)
        {
            Calls.Add("RestoreState");
        }

        public void CleanupArtifacts(DesktopTaskbarArtifacts artifacts)
        {
            Calls.Add($"CleanupArtifacts:{artifacts.ShortcutName}");
        }
    }
}
