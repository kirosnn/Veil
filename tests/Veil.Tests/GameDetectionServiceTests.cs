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

    [TestMethod]
    public void TryGetForegroundGameProcessIdForScreen_returns_process_id_for_fullscreen_parsec()
    {
        var service = new GameDetectionService();
        var processInfo = new GameDetectionService.ForegroundProcessInfo(
            77,
            "parsecd",
            @"C:\Program Files\Parsec\parsecd.exe",
            new Rect
            {
                Left = 0,
                Top = 0,
                Right = 2560,
                Bottom = 1440
            });

        int? processId = service.TryGetForegroundGameProcessIdForScreen(
            processInfo,
            new ScreenBounds(0, 0, 2560, 1440),
            []);

        Assert.AreEqual(77, processId);
    }

    [TestMethod]
    public void IsRemoteGamingProcess_returns_true_for_parsec()
    {
        bool isRemoteGamingProcess = GameDetectionService.IsRemoteGamingProcess("Parsec.exe");

        Assert.IsTrue(isRemoteGamingProcess);
    }

    [TestMethod]
    public void IsForegroundWindowFullscreenForScreen_returns_true_for_non_game_fullscreen_process()
    {
        var service = new GameDetectionService();
        var processInfo = new GameDetectionService.ForegroundProcessInfo(
            7,
            "chrome",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            new Rect
            {
                Left = 0,
                Top = 0,
                Right = 1920,
                Bottom = 1080
            },
            new IntPtr(7));

        bool isFullscreen = service.IsForegroundWindowFullscreenForScreen(
            processInfo,
            new ScreenBounds(0, 0, 1920, 1080));

        Assert.IsTrue(isFullscreen);
    }

    [TestMethod]
    public void IsForegroundWindowFullscreenForScreen_returns_false_for_snipping_overlay()
    {
        var service = new GameDetectionService();
        var processInfo = new GameDetectionService.ForegroundProcessInfo(
            18,
            "snippingtool",
            @"C:\Program Files\WindowsApps\Microsoft.ScreenSketch\SnippingTool.exe",
            new Rect
            {
                Left = 0,
                Top = 0,
                Right = 1920,
                Bottom = 1080
            },
            new IntPtr(18),
            "Windows.UI.Core.CoreWindow",
            string.Empty,
            0,
            false,
            false);

        bool isFullscreen = service.IsForegroundWindowFullscreenForScreen(
            processInfo,
            new ScreenBounds(0, 0, 1920, 1080));

        Assert.IsFalse(isFullscreen);
    }

    [TestMethod]
    public void TrySelectVisibilityProcessInfoForScreen_prefers_underlying_window_when_capture_overlay_is_topmost()
    {
        var candidates = new[]
        {
            new GameDetectionService.ForegroundProcessInfo(
                18,
                "snippingtool",
                @"C:\Program Files\WindowsApps\Microsoft.ScreenSketch\SnippingTool.exe",
                new Rect
                {
                    Left = 0,
                    Top = 0,
                    Right = 1920,
                    Bottom = 1080
                },
                new IntPtr(18),
                "Windows.UI.Core.CoreWindow",
                string.Empty,
                0,
                false,
                false),
            new GameDetectionService.ForegroundProcessInfo(
                42,
                "code",
                @"C:\Program Files\Microsoft VS Code\Code.exe",
                new Rect
                {
                    Left = 120,
                    Top = 80,
                    Right = 1720,
                    Bottom = 980
                },
                new IntPtr(42))
        };

        bool found = GameDetectionService.TrySelectVisibilityProcessInfoForScreen(
            candidates,
            new ScreenBounds(0, 0, 1920, 1080),
            out GameDetectionService.ForegroundProcessInfo processInfo);

        Assert.IsTrue(found);
        Assert.AreEqual(42, processInfo.ProcessId);
        Assert.AreEqual("code", processInfo.ProcessName);
    }

    [TestMethod]
    public void TrySelectVisibilityProcessInfoForScreen_keeps_fullscreen_game_selected_during_capture_overlay()
    {
        var screen = new ScreenBounds(0, 0, 2560, 1440);
        var candidates = new[]
        {
            new GameDetectionService.ForegroundProcessInfo(
                18,
                "screenclippinghost",
                @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\ScreenClippingHost.exe",
                new Rect
                {
                    Left = 0,
                    Top = 0,
                    Right = 2560,
                    Bottom = 1440
                },
                new IntPtr(18),
                "Windows.UI.Core.CoreWindow",
                string.Empty,
                0,
                false,
                false),
            new GameDetectionService.ForegroundProcessInfo(
                77,
                "game",
                @"C:\Games\game.exe",
                new Rect
                {
                    Left = 0,
                    Top = 0,
                    Right = 2560,
                    Bottom = 1440
                },
                new IntPtr(77))
        };

        bool found = GameDetectionService.TrySelectVisibilityProcessInfoForScreen(
            candidates,
            screen,
            out GameDetectionService.ForegroundProcessInfo processInfo);

        Assert.IsTrue(found);
        Assert.AreEqual(77, processInfo.ProcessId);
    }

    [TestMethod]
    public void TrySelectVisibilityProcessInfoForScreen_skips_transient_shell_host_surface()
    {
        var candidates = new[]
        {
            new GameDetectionService.ForegroundProcessInfo(
                24,
                "applicationframehost",
                @"C:\Windows\System32\ApplicationFrameHost.exe",
                new Rect
                {
                    Left = 0,
                    Top = 0,
                    Right = 1920,
                    Bottom = 1080
                },
                new IntPtr(24),
                "ApplicationFrameWindow",
                string.Empty,
                0,
                true,
                false),
            new GameDetectionService.ForegroundProcessInfo(
                42,
                "code",
                @"C:\Program Files\Microsoft VS Code\Code.exe",
                new Rect
                {
                    Left = 120,
                    Top = 80,
                    Right = 1720,
                    Bottom = 980
                },
                new IntPtr(42))
        };

        bool found = GameDetectionService.TrySelectVisibilityProcessInfoForScreen(
            candidates,
            new ScreenBounds(0, 0, 1920, 1080),
            out GameDetectionService.ForegroundProcessInfo processInfo);

        Assert.IsTrue(found);
        Assert.AreEqual(42, processInfo.ProcessId);
    }

    [TestMethod]
    public void TrySelectVisibilityProcessInfoForScreen_remains_stable_across_rapid_capture_transitions()
    {
        var screen = new ScreenBounds(0, 0, 1920, 1080);
        var appWindow = new GameDetectionService.ForegroundProcessInfo(
            42,
            "code",
            @"C:\Program Files\Microsoft VS Code\Code.exe",
            new Rect
            {
                Left = 120,
                Top = 80,
                Right = 1720,
                Bottom = 980
            },
            new IntPtr(42));
        var overlayWindow = new GameDetectionService.ForegroundProcessInfo(
            18,
            "snippingtool",
            @"C:\Program Files\WindowsApps\Microsoft.ScreenSketch\SnippingTool.exe",
            new Rect
            {
                Left = 0,
                Top = 0,
                Right = 1920,
                Bottom = 1080
            },
            new IntPtr(18),
            "Windows.UI.Core.CoreWindow",
            string.Empty,
            0,
            false,
            false);

        foreach (var candidates in new[]
                 {
                     new[] { appWindow },
                     new[] { overlayWindow, appWindow },
                     new[] { appWindow },
                     new[] { overlayWindow, appWindow },
                     new[] { appWindow }
                 })
        {
            bool found = GameDetectionService.TrySelectVisibilityProcessInfoForScreen(
                candidates,
                screen,
                out GameDetectionService.ForegroundProcessInfo processInfo);

            Assert.IsTrue(found);
            Assert.AreEqual(42, processInfo.ProcessId);
        }
    }
}
