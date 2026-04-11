using Veil.Services;

namespace Veil.Tests;

[TestClass]
public sealed class GamePerformanceServiceTests
{
    [TestMethod]
    public void IsGameSuspendableProcessName_returns_true_for_widgets_processes()
    {
        Assert.IsTrue(GamePerformanceService.IsGameSuspendableProcessName("WidgetService"));
        Assert.IsTrue(GamePerformanceService.IsGameSuspendableProcessName("WidgetBoard.exe"));
    }

    [TestMethod]
    public void IsGameSuspendableProcessName_returns_false_for_regular_processes()
    {
        Assert.IsFalse(GamePerformanceService.IsGameSuspendableProcessName("explorer"));
        Assert.IsFalse(GamePerformanceService.IsGameSuspendableProcessName("discord"));
    }

    [TestMethod]
    public void IsXboxBackgroundProcessName_returns_true_for_xbox_related_processes()
    {
        Assert.IsTrue(GamePerformanceService.IsXboxBackgroundProcessName("XboxPcApp"));
        Assert.IsTrue(GamePerformanceService.IsXboxBackgroundProcessName("GameBar"));
        Assert.IsTrue(GamePerformanceService.IsXboxBackgroundProcessName("GameBarPresenceWriter.exe"));
    }

    [TestMethod]
    public void IsXboxBackgroundProcessName_returns_false_for_non_xbox_processes()
    {
        Assert.IsFalse(GamePerformanceService.IsXboxBackgroundProcessName("steam"));
        Assert.IsFalse(GamePerformanceService.IsXboxBackgroundProcessName("widgetservice"));
    }

    [TestMethod]
    public void ShouldUseLaptopQuietProfileOutsideGames_returns_true_for_laptop_when_enabled()
    {
        bool shouldUseQuietProfile = GamePerformanceService.ShouldUseLaptopQuietProfileOutsideGames(
            quietLaptopOutsideGamesEnabled: true,
            isLaptopDevice: true);

        Assert.IsTrue(shouldUseQuietProfile);
    }

    [TestMethod]
    public void ShouldUseLaptopQuietProfileOutsideGames_returns_false_when_disabled()
    {
        bool shouldUseQuietProfile = GamePerformanceService.ShouldUseLaptopQuietProfileOutsideGames(
            quietLaptopOutsideGamesEnabled: false,
            isLaptopDevice: true);

        Assert.IsFalse(shouldUseQuietProfile);
    }

    [TestMethod]
    public void ShouldUseLaptopQuietProfileOutsideGames_returns_false_for_desktop_devices()
    {
        bool shouldUseQuietProfile = GamePerformanceService.ShouldUseLaptopQuietProfileOutsideGames(
            quietLaptopOutsideGamesEnabled: true,
            isLaptopDevice: false);

        Assert.IsFalse(shouldUseQuietProfile);
    }
}
