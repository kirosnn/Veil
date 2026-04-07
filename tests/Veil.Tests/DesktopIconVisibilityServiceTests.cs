using Veil.Services;

namespace Veil.Tests;

[TestClass]
public sealed class DesktopIconVisibilityServiceTests
{
    [TestMethod]
    public void ApplyLaunchState_hides_desktop_icons_when_they_are_visible()
    {
        var bridge = new FakeDesktopIconVisibilityBridge(false);
        var service = new DesktopIconVisibilityService(bridge);

        service.ApplyLaunchState();

        CollectionAssert.AreEqual(new[] { true }, bridge.RequestedStates);
    }

    [TestMethod]
    public void RestoreLaunchState_restores_initial_hidden_state()
    {
        var bridge = new FakeDesktopIconVisibilityBridge(false);
        var service = new DesktopIconVisibilityService(bridge);

        service.ApplyLaunchState();
        service.RestoreLaunchState();

        CollectionAssert.AreEqual(new[] { true, false }, bridge.RequestedStates);
    }

    [TestMethod]
    public void RestoreLaunchState_keeps_icons_hidden_when_they_started_hidden()
    {
        var bridge = new FakeDesktopIconVisibilityBridge(true);
        var service = new DesktopIconVisibilityService(bridge);

        service.ApplyLaunchState();
        service.RestoreLaunchState();

        CollectionAssert.AreEqual(new[] { true }, bridge.RequestedStates);
    }

    private sealed class FakeDesktopIconVisibilityBridge : IDesktopIconVisibilityBridge
    {
        private bool _hidden;

        internal FakeDesktopIconVisibilityBridge(bool hidden)
        {
            _hidden = hidden;
        }

        internal List<bool> RequestedStates { get; } = [];

        public bool AreDesktopIconsHidden() => _hidden;

        public void SetDesktopIconsHidden(bool hidden)
        {
            RequestedStates.Add(hidden);
            _hidden = hidden;
        }
    }
}
