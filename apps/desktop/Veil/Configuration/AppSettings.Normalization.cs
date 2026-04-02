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

    private static DesktopWidgetSetting[] NormalizeDesktopWidgets(DesktopWidgetDto[]? widgets)
    {
        if (widgets == null || widgets.Length == 0)
        {
            return [];
        }

        return widgets
            .Select(static widget => NormalizeDesktopWidget(new DesktopWidgetSetting(
                string.IsNullOrWhiteSpace(widget.Id) ? Guid.NewGuid().ToString("N") : widget.Id.Trim(),
                widget.Kind,
                widget.Title,
                widget.MonitorId,
                widget.X,
                widget.Y,
                widget.Width,
                widget.Height,
                widget.Opacity,
                widget.CornerRadius,
                widget.Scale,
                widget.Theme,
                widget.BackgroundColor,
                widget.ForegroundColor,
                widget.AccentColor,
                widget.RefreshSeconds,
                widget.ShowSeconds)))
            .GroupBy(static widget => widget.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(24)
            .ToArray();
    }

    private static DesktopWidgetSetting NormalizeDesktopWidget(DesktopWidgetSetting widget)
    {
        string kind = NormalizeDesktopWidgetKind(widget.Kind);
        string title = string.IsNullOrWhiteSpace(widget.Title)
            ? kind switch
            {
                "Clock" => "Clock",
                "Weather" => "Weather",
                _ => "System"
            }
            : widget.Title.Trim();

        return widget with
        {
            Kind = kind,
            Title = title,
            MonitorId = widget.MonitorId?.Trim() ?? string.Empty,
            X = Math.Clamp(widget.X, -7680, 7680),
            Y = Math.Clamp(widget.Y, -4320, 4320),
            Width = Math.Clamp(widget.Width, 180, 720),
            Height = Math.Clamp(widget.Height, 90, 720),
            Opacity = Math.Clamp(widget.Opacity, 0.24, 1.0),
            CornerRadius = Math.Clamp(widget.CornerRadius, 0, 48),
            Scale = Math.Clamp(widget.Scale, 0.7, 1.8),
            Theme = NormalizePanelTheme(widget.Theme, "Dark"),
            BackgroundColor = NormalizeHexColor(widget.BackgroundColor),
            ForegroundColor = NormalizeHexColor(widget.ForegroundColor),
            AccentColor = NormalizeHexColor(widget.AccentColor),
            RefreshSeconds = Math.Clamp(widget.RefreshSeconds, 1, 120),
            ShowSeconds = widget.ShowSeconds
        };
    }

    private static string NormalizeDesktopWidgetKind(string? kind)
    {
        return kind is "Clock" or "Weather" or "System" ? kind : "Clock";
    }

    private static string NormalizeHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "#000000";
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
            return "#000000";
        }

        foreach (char c in value[1..])
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return "#000000";
            }
        }

        return value.ToUpperInvariant();
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

    private static string[] NormalizeWeatherCities(string[]? cityNames)
    {
        if (cityNames == null)
        {
            return
            [
                "Cupertino",
                "New York",
                "London",
                "Beijing"
            ];
        }

        if (cityNames.Length == 0)
        {
            return [];
        }

        return cityNames
            .Select(static name => name.Trim())
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
    }

    private static string NormalizeTopBarDisplayMode(string? value)
    {
        return value is "Primary" or "All" or "Custom" ? value : "Primary";
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

    private static string InferGlobalPanelTheme(AppSettingsDto dto)
    {
        if (dto.MusicPanelTheme is "Light" || dto.RunCatPanelTheme is "Light" || dto.WeatherPanelTheme is "Light")
        {
            return "Light";
        }

        return "Dark";
    }
}
