using Veil.Interop;
using Veil.Services;

namespace Veil.Tests;

[TestClass]
public sealed class WindowSwitcherServiceTests
{
    [TestMethod]
    public void HasSwitchableExtendedStyles_rejects_non_activating_windows()
    {
        Assert.IsFalse(WindowSwitcherService.HasSwitchableExtendedStyles(NativeMethods.WS_EX_NOACTIVATE));
    }

    [TestMethod]
    public void HasSwitchableExtendedStyles_rejects_tool_windows_without_appwindow()
    {
        Assert.IsFalse(WindowSwitcherService.HasSwitchableExtendedStyles(NativeMethods.WS_EX_TOOLWINDOW));
    }

    [TestMethod]
    public void HasSwitchableExtendedStyles_accepts_regular_app_windows()
    {
        Assert.IsTrue(WindowSwitcherService.HasSwitchableExtendedStyles(NativeMethods.WS_EX_APPWINDOW));
    }

    [TestMethod]
    public void HasMeaningfulBounds_rejects_zero_sized_windows()
    {
        Assert.IsFalse(WindowSwitcherService.HasMeaningfulBounds(new NativeMethods.Rect
        {
            Left = 200,
            Top = 200,
            Right = 200,
            Bottom = 200
        }));
    }

    [TestMethod]
    public void HasMeaningfulBounds_accepts_visible_window_area()
    {
        Assert.IsTrue(WindowSwitcherService.HasMeaningfulBounds(new NativeMethods.Rect
        {
            Left = 100,
            Top = 100,
            Right = 500,
            Bottom = 300
        }));
    }
}
