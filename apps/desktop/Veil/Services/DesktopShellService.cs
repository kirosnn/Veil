namespace Veil.Services;

internal readonly record struct RegistryStringValueSnapshot(bool Exists, string? Value);

internal readonly record struct RegistryDwordValueSnapshot(bool Exists, int Value);

internal readonly record struct DesktopShellState(
    bool DesktopIconsHidden,
    bool TaskbarsHidden,
    RegistryStringValueSnapshot StartLayoutFile,
    RegistryDwordValueSnapshot LockedStartLayout,
    RegistryDwordValueSnapshot RecycleBinNewStartPanel,
    RegistryDwordValueSnapshot RecycleBinClassicStartMenu);

internal readonly record struct DesktopTaskbarArtifacts(
    string ShortcutName,
    string ShortcutPath,
    string? ShortcutBackupPath,
    string LauncherScriptPath,
    string TaskbarPolicyPath);

internal interface IDesktopShellBridge
{
    DesktopShellState CaptureState();
    DesktopTaskbarArtifacts PrepareTaskbarArtifacts();
    void ApplyTaskbarPolicy(DesktopTaskbarArtifacts artifacts);
    void SetDesktopIconsHidden(bool hidden);
    void SetRecycleBinDesktopIconHidden(bool hidden);
    void RestartExplorer();
    bool IsTaskbarShortcutPinned(string shortcutName);
    void RestoreState(DesktopShellState state);
    void CleanupArtifacts(DesktopTaskbarArtifacts artifacts);
}

internal sealed class DesktopShellService
{
    private readonly IDesktopShellBridge _bridge;
    private DesktopShellState _appliedState;
    private DesktopTaskbarArtifacts _appliedArtifacts;
    private bool _hasAppliedState;

    internal DesktopShellService(IDesktopShellBridge bridge)
    {
        _bridge = bridge;
    }

    internal bool TryApplyLaunchState()
    {
        DesktopShellState state = _bridge.CaptureState();
        DesktopTaskbarArtifacts artifacts = _bridge.PrepareTaskbarArtifacts();

        try
        {
            _bridge.ApplyTaskbarPolicy(artifacts);
            _bridge.SetDesktopIconsHidden(true);
            _bridge.SetRecycleBinDesktopIconHidden(true);
            _bridge.RestartExplorer();

            if (!_bridge.IsTaskbarShortcutPinned(artifacts.ShortcutName))
            {
                _bridge.RestoreState(state);
                _bridge.RestartExplorer();
                _bridge.CleanupArtifacts(artifacts);
                return false;
            }

            _appliedState = state;
            _appliedArtifacts = artifacts;
            _hasAppliedState = true;
            return true;
        }
        catch
        {
            try
            {
                _bridge.RestoreState(state);
            }
            catch
            {
            }

            try
            {
                _bridge.CleanupArtifacts(artifacts);
            }
            catch
            {
            }

            throw;
        }
    }

    internal void RestoreLaunchState()
    {
        if (!_hasAppliedState)
        {
            return;
        }

        try
        {
            _bridge.RestoreState(_appliedState);
            _bridge.RestartExplorer();
        }
        finally
        {
            _bridge.CleanupArtifacts(_appliedArtifacts);
            _hasAppliedState = false;
        }
    }
}
