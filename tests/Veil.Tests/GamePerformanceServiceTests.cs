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

    [TestMethod]
    public void ShouldCompactVeilHeapOnModeTransition_returns_true_only_when_entering_game_mode()
    {
        Assert.IsTrue(GamePerformanceService.ShouldCompactVeilHeapOnModeTransition(
            GamePerformanceService.VeilOptimizationMode.None,
            GamePerformanceService.VeilOptimizationMode.Game));
        Assert.IsTrue(GamePerformanceService.ShouldCompactVeilHeapOnModeTransition(
            GamePerformanceService.VeilOptimizationMode.Background,
            GamePerformanceService.VeilOptimizationMode.Game));
        Assert.IsFalse(GamePerformanceService.ShouldCompactVeilHeapOnModeTransition(
            GamePerformanceService.VeilOptimizationMode.Game,
            GamePerformanceService.VeilOptimizationMode.Game));
        Assert.IsFalse(GamePerformanceService.ShouldCompactVeilHeapOnModeTransition(
            GamePerformanceService.VeilOptimizationMode.Game,
            GamePerformanceService.VeilOptimizationMode.Background));
    }

    [TestMethod]
    public void ShouldTrimVeilWorkingSetOnModeTransition_returns_false_without_real_transition()
    {
        DateTime nowUtc = new(2026, 4, 12, 10, 0, 0, DateTimeKind.Utc);
        DateTime lastTrimUtc = nowUtc - TimeSpan.FromMinutes(30);

        Assert.IsFalse(GamePerformanceService.ShouldTrimVeilWorkingSetOnModeTransition(
            GamePerformanceService.VeilOptimizationMode.None,
            GamePerformanceService.VeilOptimizationMode.None,
            nowUtc,
            lastTrimUtc));
        Assert.IsFalse(GamePerformanceService.ShouldTrimVeilWorkingSetOnModeTransition(
            GamePerformanceService.VeilOptimizationMode.Background,
            GamePerformanceService.VeilOptimizationMode.Background,
            nowUtc,
            lastTrimUtc));
    }

    [TestMethod]
    public void ShouldTrimVeilWorkingSetOnModeTransition_respects_trim_cooldown()
    {
        DateTime nowUtc = new(2026, 4, 12, 10, 0, 0, DateTimeKind.Utc);

        Assert.IsTrue(GamePerformanceService.ShouldTrimVeilWorkingSetOnModeTransition(
            GamePerformanceService.VeilOptimizationMode.None,
            GamePerformanceService.VeilOptimizationMode.Background,
            nowUtc,
            nowUtc - TimeSpan.FromMinutes(5)));
        Assert.IsFalse(GamePerformanceService.ShouldTrimVeilWorkingSetOnModeTransition(
            GamePerformanceService.VeilOptimizationMode.Background,
            GamePerformanceService.VeilOptimizationMode.Game,
            nowUtc,
            nowUtc - TimeSpan.FromSeconds(30)));
    }
}
