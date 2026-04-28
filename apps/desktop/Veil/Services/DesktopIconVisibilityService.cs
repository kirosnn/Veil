namespace Veil.Services;

internal interface IDesktopIconVisibilityBridge
{
    bool AreDesktopIconsHidden();
    void SetDesktopIconsHidden(bool hidden);
}

internal sealed class DesktopIconVisibilityService
{
    private readonly IDesktopIconVisibilityBridge _bridge;
    private bool _capturedDesktopIconsHidden;
    private bool _hasCapturedState;

    internal DesktopIconVisibilityService(IDesktopIconVisibilityBridge bridge)
    {
        _bridge = bridge;
    }

    internal void ApplyLaunchState()
    {
        _capturedDesktopIconsHidden = _bridge.AreDesktopIconsHidden();
        _hasCapturedState = true;
        if (!_capturedDesktopIconsHidden)
        {
            _bridge.SetDesktopIconsHidden(true);
        }
    }

    internal void RestoreLaunchState()
    {
        if (!_hasCapturedState)
        {
            return;
        }

        try
        {
            _bridge.SetDesktopIconsHidden(_capturedDesktopIconsHidden);
        }
        finally
        {
            _hasCapturedState = false;
        }
    }
}
