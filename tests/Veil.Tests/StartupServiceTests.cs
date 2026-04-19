namespace Veil.Tests;

[TestClass]
public sealed class StartupServiceTests
{
    [TestMethod]
    public void IsStartupLaunch_ReturnsTrueWhenArgumentExistsAmongOthers()
    {
        bool result = Veil.Services.StartupService.IsStartupLaunch("--foo --startup --bar");

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void IsStartupLaunch_ReturnsTrueForStartupArgumentAlone()
    {
        bool result = Veil.Services.StartupService.IsStartupLaunch("--startup");

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void IsStartupLaunch_ReturnsFalseWhenArgumentAbsent()
    {
        bool result = Veil.Services.StartupService.IsStartupLaunch("--foo --bar");

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void IsStartupLaunch_ReturnsFalseForNullOrEmpty()
    {
        Assert.IsFalse(Veil.Services.StartupService.IsStartupLaunch(null));
        Assert.IsFalse(Veil.Services.StartupService.IsStartupLaunch(""));
        Assert.IsFalse(Veil.Services.StartupService.IsStartupLaunch("   "));
    }
}
