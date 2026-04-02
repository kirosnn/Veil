using Veil.Services;
using Veil.Interop;
using static Veil.Interop.NativeMethods;

namespace Veil.Tests;

[TestClass]
public sealed class GameDetectionServiceTests
{
    [TestMethod]
    public void ShouldIgnoreForegroundWindowClassForGameDetection_returns_true_for_workerw()
    {
        bool ignored = GameDetectionService.ShouldIgnoreForegroundWindowClassForGameDetection("WorkerW");

        Assert.IsTrue(ignored);
    }

    [TestMethod]
    public void ShouldIgnoreForegroundWindowClassForGameDetection_returns_false_for_regular_app_window()
    {
        bool ignored = GameDetectionService.ShouldIgnoreForegroundWindowClassForGameDetection("ChromeWindowClass");

        Assert.IsFalse(ignored);
    }

    [TestMethod]
    public void TryGetForegroundGameProcessIdForScreen_returns_process_id_for_configured_fullscreen_process()
    {
        var service = new GameDetectionService();
        var processInfo = new GameDetectionService.ForegroundProcessInfo(
            42,
            "game",
            @"C:\Games\game.exe",
            new Rect
            {
                Left = 0,
                Top = 0,
                Right = 1920,
                Bottom = 1080
            });

        int? processId = service.TryGetForegroundGameProcessIdForScreen(
            processInfo,
            new ScreenBounds(0, 0, 1920, 1080),
            ["game"]);

        Assert.AreEqual(42, processId);
    }

    [TestMethod]
    public void TryGetForegroundGameProcessIdForScreen_returns_null_for_non_game_fullscreen_process()
    {
        var service = new GameDetectionService();
        var processInfo = new GameDetectionService.ForegroundProcessInfo(
            7,
            "code",
            @"C:\Program Files\Microsoft VS Code\Code.exe",
            new Rect
            {
                Left = 0,
                Top = 0,
                Right = 1920,
                Bottom = 1080
            });

        int? processId = service.TryGetForegroundGameProcessIdForScreen(
            processInfo,
            new ScreenBounds(0, 0, 1920, 1080),
            []);

        Assert.IsNull(processId);
    }
}
