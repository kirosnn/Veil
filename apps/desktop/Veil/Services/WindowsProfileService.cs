using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Veil.Configuration;
using Veil.Diagnostics;
using Veil.Interop;

namespace Veil.Services;

internal static class WindowsProfileService
{
    private static readonly Dictionary<PowerPlanPreset, string> PowerPlanGuids = new()
    {
        [PowerPlanPreset.Balanced] = "381b4222-f694-41f0-9685-ff5bb260df2e",
        [PowerPlanPreset.HighPerformance] = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
        [PowerPlanPreset.PowerSaver] = "a1841308-3541-4fab-bc81-f71556f20b4a",
        [PowerPlanPreset.UltimatePerformance] = "e9a42b02-d5df-448d-aa00-03f14749eb61",
        [PowerPlanPreset.BestEfficiency] = "961cc777-2547-4f9d-8174-7d86181b8a7a"
    };

    internal static async Task ApplyAsync(WindowsProfile profile)
    {
        if (profile.PowerPlan.HasValue)
            await SetPowerPlanAsync(profile.PowerPlan.Value);

        if (profile.CpuMaxPercent.HasValue)
            await SetCpuMaxAsync(profile.CpuMaxPercent.Value);

        if (profile.Displays is { Count: > 0 })
            RestoreDisplayProfiles(profile.Displays);
        else if (profile.RefreshRateHz.HasValue)
            SetRefreshRate(profile.RefreshRateHz.Value);

        if (profile.TransparencyEnabled.HasValue)
            SetTransparency(profile.TransparencyEnabled.Value);

        if (profile.AnimationsEnabled.HasValue)
            SetAnimations(profile.AnimationsEnabled.Value);
    }

    internal static async Task<WindowsProfile> CaptureCurrentAsync()
    {
        return new WindowsProfile
        {
            Id = "base-windows-profile",
            Name = "Previous Windows State",
            PowerPlan = await GetCurrentPowerPlanAsync(),
            CpuMaxPercent = await GetCurrentCpuMaxAsync(),
            RefreshRateHz = GetCurrentRefreshRate(),
            Displays = GetCurrentDisplayProfiles(),
            TransparencyEnabled = GetTransparency(),
            AnimationsEnabled = GetAnimations()
        };
    }

    internal static async Task RestoreWindowsDefaultsAsync()
    {
        var store = WindowsProfileStore.Current;
        var profile = store.BaseProfile ?? new WindowsProfile
        {
            PowerPlan = PowerPlanPreset.Balanced,
            CpuMaxPercent = 100,
            RefreshRateHz = 120,
            TransparencyEnabled = true,
            AnimationsEnabled = true
        };

        await ApplyAsync(profile);
        store.ClearBaseProfile();
    }

    private static async Task SetPowerPlanAsync(PowerPlanPreset preset)
    {
        if (!PowerPlanGuids.TryGetValue(preset, out var guid)) return;

        int result = await RunPowercfgAsync($"/setactive {guid}");

        if (result != 0 && preset == PowerPlanPreset.UltimatePerformance)
        {
            await RunPowercfgAsync("/duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61");
            await RunPowercfgAsync("/setactive e9a42b02-d5df-448d-aa00-03f14749eb61");
        }
    }

    private static async Task SetCpuMaxAsync(int percent)
    {
        percent = Math.Clamp(percent, 1, 100);
        const string proc = "54533251-82be-4824-96c1-47b60b740d00";
        const string maxState = "bc5038f7-23e0-4960-96da-33abaf5935ec";
        await RunPowercfgAsync($"/setacvalueindex scheme_current {proc} {maxState} {percent}");
        await RunPowercfgAsync($"/setdcvalueindex scheme_current {proc} {maxState} {percent}");
        await RunPowercfgAsync("/setactive scheme_current");
    }

    private static async Task<PowerPlanPreset?> GetCurrentPowerPlanAsync()
    {
        string output = await RunPowercfgCaptureAsync("/getactivescheme");
        foreach (var pair in PowerPlanGuids)
        {
            if (output.Contains(pair.Value, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Key;
            }
        }

        return null;
    }

    private static async Task<int?> GetCurrentCpuMaxAsync()
    {
        const string proc = "54533251-82be-4824-96c1-47b60b740d00";
        const string maxState = "bc5038f7-23e0-4960-96da-33abaf5935ec";
        string output = await RunPowercfgCaptureAsync($"/query scheme_current {proc} {maxState}");

        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains("AC", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("CA", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Match match = Regex.Match(line, @"0x([0-9a-fA-F]+)");
            if (match.Success
                && int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out int hexPercent))
            {
                return Math.Clamp(hexPercent, 1, 100);
            }

            match = Regex.Match(line, @"\b([0-9]{1,3})\b");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int percent))
            {
                return Math.Clamp(percent, 1, 100);
            }
        }

        return null;
    }

    private static int? GetCurrentRefreshRate()
    {
        return GetCurrentDisplayProfiles()
            .FirstOrDefault(static display => display.RefreshRateHz > 0)
            ?.RefreshRateHz;
    }

    private static void SetRefreshRate(int targetHz)
    {
        try
        {
            foreach (var display in EnumerateAttachedDisplayDevices())
            {
                SetDisplayRefreshRate(display.DeviceName, targetHz);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to set refresh rate.", ex);
        }
    }

    private static void SetDisplayRefreshRate(string deviceName, int targetHz)
    {
        var currentDm = NativeMethods.DEVMODE.Create();
        if (!NativeMethods.EnumDisplaySettingsExW(deviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref currentDm, 0))
            return;

        int currentWidth = (int)currentDm.dmPelsWidth;
        int currentHeight = (int)currentDm.dmPelsHeight;
        int bestModeIdx = -1;
        int closestDiff = int.MaxValue;

        for (uint i = 0; ; i++)
        {
            var mode = NativeMethods.DEVMODE.Create();
            if (!NativeMethods.EnumDisplaySettingsExW(deviceName, i, ref mode, 0)) break;
            if ((int)mode.dmPelsWidth != currentWidth || (int)mode.dmPelsHeight != currentHeight) continue;

            int diff = Math.Abs((int)mode.dmDisplayFrequency - targetHz);
            if (diff < closestDiff)
            {
                closestDiff = diff;
                bestModeIdx = (int)i;
            }
        }

        if (bestModeIdx < 0) return;

        var bestMode = NativeMethods.DEVMODE.Create();
        if (!NativeMethods.EnumDisplaySettingsExW(deviceName, (uint)bestModeIdx, ref bestMode, 0))
        {
            return;
        }

        ApplyDisplayMode(deviceName, ref bestMode);
    }

    private static List<WindowsDisplayProfile> GetCurrentDisplayProfiles()
    {
        var displays = new List<WindowsDisplayProfile>();

        try
        {
            foreach (var display in EnumerateAttachedDisplayDevices())
            {
                var currentDm = NativeMethods.DEVMODE.Create();
                if (!NativeMethods.EnumDisplaySettingsExW(display.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref currentDm, 0))
                {
                    continue;
                }

                displays.Add(new WindowsDisplayProfile
                {
                    DeviceName = display.DeviceName,
                    DisplayName = display.DeviceString,
                    Width = (int)currentDm.dmPelsWidth,
                    Height = (int)currentDm.dmPelsHeight,
                    RefreshRateHz = (int)currentDm.dmDisplayFrequency
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to read display profiles.", ex);
        }

        return displays;
    }

    private static void RestoreDisplayProfiles(IEnumerable<WindowsDisplayProfile> displays)
    {
        bool changed = false;
        foreach (var display in displays)
        {
            if (string.IsNullOrWhiteSpace(display.DeviceName) || display.RefreshRateHz <= 0)
            {
                continue;
            }

            changed |= RestoreDisplayProfile(display);
        }

        if (changed)
        {
            NativeMethods.ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        }
    }

    private static bool RestoreDisplayProfile(WindowsDisplayProfile display)
    {
        int bestModeIdx = -1;
        int closestDiff = int.MaxValue;

        for (uint i = 0; ; i++)
        {
            var mode = NativeMethods.DEVMODE.Create();
            if (!NativeMethods.EnumDisplaySettingsExW(display.DeviceName, i, ref mode, 0)) break;
            if ((int)mode.dmPelsWidth != display.Width || (int)mode.dmPelsHeight != display.Height) continue;

            int diff = Math.Abs((int)mode.dmDisplayFrequency - display.RefreshRateHz);
            if (diff < closestDiff)
            {
                closestDiff = diff;
                bestModeIdx = (int)i;
            }
        }

        if (bestModeIdx < 0) return false;

        var bestMode = NativeMethods.DEVMODE.Create();
        if (!NativeMethods.EnumDisplaySettingsExW(display.DeviceName, (uint)bestModeIdx, ref bestMode, 0))
        {
            return false;
        }

        return PrepareDisplayMode(display.DeviceName, ref bestMode);
    }

    private static void ApplyDisplayMode(string deviceName, ref NativeMethods.DEVMODE mode)
    {
        if (PrepareDisplayMode(deviceName, ref mode))
        {
            NativeMethods.ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        }
    }

    private static bool PrepareDisplayMode(string deviceName, ref NativeMethods.DEVMODE mode)
    {
        int result = NativeMethods.ChangeDisplaySettingsExW(
            deviceName,
            ref mode,
            IntPtr.Zero,
            NativeMethods.CDS_UPDATEREGISTRY | NativeMethods.CDS_NORESET,
            IntPtr.Zero);

        if (result != NativeMethods.DISP_CHANGE_SUCCESSFUL)
        {
            AppLogger.Info($"Display mode change for {deviceName} returned {result}.");
            return false;
        }

        return true;
    }

    private static IEnumerable<NativeMethods.DISPLAY_DEVICE> EnumerateAttachedDisplayDevices()
    {
        var dd = NativeMethods.DISPLAY_DEVICE.Create();
        for (uint i = 0; NativeMethods.EnumDisplayDevicesW(null, i, ref dd, 0); i++)
        {
            if ((dd.StateFlags & NativeMethods.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
            {
                yield return dd;
            }

            dd = NativeMethods.DISPLAY_DEVICE.Create();
        }
    }

    private static void SetTransparency(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: true);
            key?.SetValue("EnableTransparency", enabled ? 1 : 0, RegistryValueKind.DWord);

            var lpSection = Marshal.StringToHGlobalUni("ImmersiveColorSet");
            try
            {
                NativeMethods.SendMessageTimeoutW(
                    new IntPtr(0xFFFF),
                    NativeMethods.WM_SETTINGCHANGE,
                    IntPtr.Zero,
                    lpSection,
                    NativeMethods.SMTO_ABORTIFHUNG,
                    3000,
                    out _);
            }
            finally
            {
                Marshal.FreeHGlobal(lpSection);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to set transparency.", ex);
        }
    }

    private static bool? GetTransparency()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            return key?.GetValue("EnableTransparency") is int value
                ? value != 0
                : null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to read transparency.", ex);
            return null;
        }
    }

    private static void SetAnimations(bool enabled)
    {
        try
        {
            var info = new NativeMethods.ANIMATIONINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.ANIMATIONINFO>(),
                iMinAnimate = enabled ? 1 : 0
            };
            NativeMethods.SystemParametersInfoAnimationW(
                NativeMethods.SPI_SETANIMATION,
                (uint)Marshal.SizeOf<NativeMethods.ANIMATIONINFO>(),
                ref info,
                NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to set animations.", ex);
        }
    }

    private static bool? GetAnimations()
    {
        try
        {
            var info = new NativeMethods.ANIMATIONINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.ANIMATIONINFO>()
            };

            if (!NativeMethods.SystemParametersInfoAnimationW(
                NativeMethods.SPI_GETANIMATION,
                (uint)Marshal.SizeOf<NativeMethods.ANIMATIONINFO>(),
                ref info,
                0))
            {
                return null;
            }

            return info.iMinAnimate != 0;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to read animations.", ex);
            return null;
        }
    }

    private static async Task<int> RunPowercfgAsync(string args)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powercfg",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            proc.Start();
            await proc.WaitForExitAsync();
            AppLogger.Info($"powercfg {args} → exit {proc.ExitCode}");
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"powercfg {args} failed.", ex);
            return -1;
        }
    }

    private static async Task<string> RunPowercfgCaptureAsync(string args)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powercfg",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            proc.Start();
            string output = await proc.StandardOutput.ReadToEndAsync();
            string error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            AppLogger.Info($"powercfg {args} -> exit {proc.ExitCode}");
            return string.Concat(output, error);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"powercfg {args} failed.", ex);
            return string.Empty;
        }
    }
}
