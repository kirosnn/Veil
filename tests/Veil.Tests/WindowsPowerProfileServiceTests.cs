using Veil.Services;

namespace Veil.Tests;

[TestClass]
public sealed class WindowsPowerProfileServiceTests
{
    [TestMethod]
    public void ResolveQuietSchemeGuid_returns_balanced_scheme_on_ac_power()
    {
        Guid schemeGuid = WindowsPowerProfileService.ResolveQuietSchemeGuid(onAcPower: true);

        Assert.AreEqual(new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"), schemeGuid);
    }

    [TestMethod]
    public void ResolveQuietSchemeGuid_returns_power_saver_scheme_on_battery()
    {
        Guid schemeGuid = WindowsPowerProfileService.ResolveQuietSchemeGuid(onAcPower: false);

        Assert.AreEqual(new Guid("a1841308-3541-4fab-bc81-f71556f20b4a"), schemeGuid);
    }

    [TestMethod]
    public void ResolveQuietSchemeGuid_returns_balanced_scheme_for_high_end_laptop_on_battery()
    {
        Guid schemeGuid = WindowsPowerProfileService.ResolveQuietSchemeGuid(
            onAcPower: false,
            logicalProcessorCount: 16,
            totalMemoryGb: 32);

        Assert.AreEqual(new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"), schemeGuid);
    }

    [TestMethod]
    public void ResolveQuietSchemeGuid_returns_power_saver_scheme_for_compact_laptop_on_battery()
    {
        Guid schemeGuid = WindowsPowerProfileService.ResolveQuietSchemeGuid(
            onAcPower: false,
            logicalProcessorCount: 4,
            totalMemoryGb: 8);

        Assert.AreEqual(new Guid("a1841308-3541-4fab-bc81-f71556f20b4a"), schemeGuid);
    }

    [TestMethod]
    public void ResolveQuietSchemeGuid_returns_balanced_scheme_for_midrange_laptop_on_battery()
    {
        Guid schemeGuid = WindowsPowerProfileService.ResolveQuietSchemeGuid(
            onAcPower: false,
            logicalProcessorCount: 8,
            totalMemoryGb: 16);

        Assert.AreEqual(new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"), schemeGuid);
    }

    [TestMethod]
    public void IsPortablePowerManagedDevice_returns_true_when_lid_is_present()
    {
        bool isPortable = WindowsPowerProfileService.IsPortablePowerManagedDevice(
            lidPresent: true,
            systemBatteriesPresent: false);

        Assert.IsTrue(isPortable);
    }

    [TestMethod]
    public void IsPortablePowerManagedDevice_returns_true_when_system_battery_is_present()
    {
        bool isPortable = WindowsPowerProfileService.IsPortablePowerManagedDevice(
            lidPresent: false,
            systemBatteriesPresent: true);

        Assert.IsTrue(isPortable);
    }

    [TestMethod]
    public void IsPortablePowerManagedDevice_returns_false_for_non_portable_hardware()
    {
        bool isPortable = WindowsPowerProfileService.IsPortablePowerManagedDevice(
            lidPresent: false,
            systemBatteriesPresent: false);

        Assert.IsFalse(isPortable);
    }
}
