using Veil.Interop;
using Veil.Services;

namespace Veil.Configuration;

internal sealed partial class AppSettings
{
    private static AppShortcutSetting?[] NormalizeShortcutButtons(AppShortcutDto?[]? shortcutButtons)
    {
        var slots = new AppShortcutSetting?[MaxShortcutButtons];

        if (shortcutButtons == null)
        {
            return slots;
        }

        for (int i = 0; i < Math.Min(shortcutButtons.Length, MaxShortcutButtons); i++)
        {
            var shortcut = shortcutButtons[i];
            if (shortcut is null || string.IsNullOrWhiteSpace(shortcut.Name) || string.IsNullOrWhiteSpace(shortcut.AppId))
            {
                continue;
            }

            string appName = shortcut.Name.Trim();
            string appId = shortcut.AppId.Trim();
            string displayName = string.IsNullOrWhiteSpace(shortcut.DisplayName)
                ? appName
                : shortcut.DisplayName.Trim();
            slots[i] = new AppShortcutSetting(appName, appId, displayName);
        }

        return slots;
    }

    internal static bool TryNormalizeHexColor(string? value, out string normalizedColor)
    {
        normalizedColor = "#000000";

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        if (value.Length == 4)
        {
            value = $"#{value[1]}{value[1]}{value[2]}{value[2]}{value[3]}{value[3]}";
        }

        if (value.Length != 7)
        {
            return false;
        }

        foreach (char c in value[1..])
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        normalizedColor = value.ToUpperInvariant();
        return true;
    }

    private static string NormalizeHexColor(string? value)
    {
        return TryNormalizeHexColor(value, out string normalizedColor)
            ? normalizedColor
            : "#000000";
    }

    private static string NormalizeTopBarStyle(string? value)
    {
        if (string.Equals(value, "Clear", StringComparison.OrdinalIgnoreCase))
        {
            return "Transparent";
        }

        return value is "Solid" or "Blur" or "Adaptive" or "Transparent" ? value : "Solid";
    }

    private static double NormalizeTopBarOpacityForStyle(double value, string topBarStyle, bool useDefaultWhenInvisible)
    {
        value = Math.Clamp(value, 0.0, 1.0);
        if (topBarStyle == "Transparent")
        {
            return value;
        }

        if (value <= 0.001)
        {
            return useDefaultWhenInvisible ? DefaultTopBarOpacity : MinimumVisibleTopBarOpacity;
        }

        return Math.Max(value, MinimumVisibleTopBarOpacity);
    }

    private static double NormalizeBlurIntensityForStyle(double value, string topBarStyle, bool useDefaultWhenInvisible)
    {
        value = Math.Clamp(value, 0.0, 1.0);
        if (topBarStyle != "Blur")
        {
            return value;
        }

        if (value <= 0.001)
        {
            return useDefaultWhenInvisible ? DefaultBlurIntensity : MinimumVisibleBlurIntensity;
        }

        return Math.Max(value, MinimumVisibleBlurIntensity);
    }

    private static string[] NormalizeGameProcessNames(string[]? processNames)
    {
        if (processNames == null)
        {
            return [];
        }

        return processNames
            .Select(GameProcessMonitor.NormalizeProcessName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeTopBarDisplayMode(string? value)
    {
        return value is "Primary" or "All" or "Custom" ? value : "Primary";
    }

    private static string NormalizeAiProvider(string? value)
    {
        return AiProviderKind.Normalize(value);
    }

    private static string NormalizeAiSettingText(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();
    }

    private static string NormalizeLocalSpeechModelId(string? value)
    {
        return LocalSpeechModelCatalog.Normalize(value);
    }

    private static string[] NormalizeTopBarMonitorIds(string[]? monitorIds)
    {
        if (monitorIds == null)
        {
            return [];
        }

        return monitorIds
            .Select(static id => id.Trim())
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GetPrimaryMonitorId(IReadOnlyList<MonitorInfo2> monitors)
    {
        return monitors.FirstOrDefault(static monitor => monitor.IsPrimary)?.Id
            ?? monitors[0].Id;
    }

    private IReadOnlyList<string> ResolveCustomTopBarMonitorIds(IReadOnlyList<MonitorInfo2> monitors)
    {
        string[] availableIds = monitors
            .Select(static monitor => monitor.Id)
            .ToArray();

        string[] selectedIds = _topBarMonitorIds
            .Where(id => availableIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (selectedIds.Length > 0)
        {
            return selectedIds;
        }

        return [GetPrimaryMonitorId(monitors)];
    }

    private static string NormalizePanelTheme(string? value, string fallback = "Dark")
    {
        return value is "Light" or "Dark" ? value : fallback;
    }

}
