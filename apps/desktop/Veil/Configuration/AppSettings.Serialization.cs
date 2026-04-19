using System.Text.Json;
using Veil.Diagnostics;
using Veil.Services;

namespace Veil.Configuration;

internal sealed partial class AppSettings
{
    internal void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var payload = new AppSettingsDto
        {
            TopBarOpacity = _topBarOpacity,
            ClockOffset = _clockOffset,
            ClockBalance = _clockBalance,
            TopBarContentAlignment = _topBarContentAlignment,
            MenuTintOpacity = _menuTintOpacity,
            FinderBubbleOpacity = _finderBubbleOpacity,
            ShowFinderBubble = _showFinderBubble,
            FinderHotkeyEnabled = _finderHotkeyEnabled,
            DiscordButtonEnabled = _discordButtonEnabled,
            MusicButtonEnabled = _musicButtonEnabled,
            MusicShowVolume = _musicShowVolume,
            MusicShowSourceToggle = _musicShowSourceToggle,
            MusicPanelTheme = _musicPanelTheme,
            RunCatPanelTheme = _runCatPanelTheme,
            TopBarStyle = _topBarStyle,
            SolidColor = _solidColor,
            TopBarForegroundColor = _topBarForegroundColor,
            BlurIntensity = _blurIntensity,
            RunCatEnabled = _runCatEnabled,
            RunCatRunner = _runCatRunner,
            TopBarDisplayMode = _topBarDisplayMode,
            TopBarMonitorIds = _topBarMonitorIds,
            ShowAppButtonOutline = _showAppButtonOutline,
            LocalSpeechModelId = _localSpeechModelId,
            GameDetectionMode = _gameDetectionMode,
            GameProcessNames = _gameProcessNames,
            BackgroundOptimizationEnabled = _backgroundOptimizationEnabled,
            SystemPowerBoostEnabled = _systemPowerBoostEnabled,
            QuietLaptopOutsideGamesEnabled = _quietLaptopOutsideGamesEnabled,
            HideForFullscreen = _hideForFullscreen,
            ShortcutButtons = _shortcutButtons
                .Select(setting => setting is null
                    ? null
                    : new AppShortcutDto
                    {
                        Name = setting.AppName,
                        AppId = setting.AppId,
                        DisplayName = setting.DisplayName
                    })
                .ToArray(),
            TerminalDefaultProfileId = _terminalDefaultProfileId,
            TerminalFontFamily       = _terminalFontFamily,
            TerminalFontSize         = _terminalFontSize,
            TerminalCursorStyle      = _terminalCursorStyle,
            TerminalScrollback       = _terminalScrollback,
            TerminalCols             = _terminalCols,
            TerminalRows             = _terminalRows,
        };

        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(payload, _jsonOptions));
    }

    private static AppSettings LoadOrCreate()
    {
        var directoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Veil");
        var settingsPath = Path.Combine(directoryPath, "settings.json");
        var settings = new AppSettings(settingsPath);

        if (!File.Exists(settingsPath))
        {
            settings._isFirstLaunch = true;
            settings.Save();
            return settings;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<AppSettingsDto>(File.ReadAllText(settingsPath));
            if (dto != null)
            {
                bool requiresSave = false;
                string normalizedTopBarStyle = NormalizeTopBarStyle(dto.TopBarStyle);
                double normalizedTopBarOpacity = NormalizeTopBarOpacityForStyle(dto.TopBarOpacity, normalizedTopBarStyle, useDefaultWhenInvisible: true);
                double normalizedBlurIntensity = NormalizeBlurIntensityForStyle(dto.BlurIntensity, normalizedTopBarStyle, useDefaultWhenInvisible: true);

                requiresSave |= !string.Equals(dto.TopBarStyle, normalizedTopBarStyle, StringComparison.Ordinal);
                requiresSave |= Math.Abs(Math.Clamp(dto.TopBarOpacity, 0.0, 1.0) - normalizedTopBarOpacity) >= 0.001;
                requiresSave |= Math.Abs(Math.Clamp(dto.BlurIntensity, 0.0, 1.0) - normalizedBlurIntensity) >= 0.001;

                settings._topBarOpacity = normalizedTopBarOpacity;
                settings._clockOffset = Math.Clamp(dto.ClockOffset, -40, 40);
                settings._clockBalance = Math.Clamp(dto.ClockBalance, 0.0, 1.0);
                settings._topBarContentAlignment = NormalizeTopBarContentAlignment(dto.TopBarContentAlignment);
                settings._menuTintOpacity = Math.Clamp(dto.MenuTintOpacity, 0.04, 0.3);
                settings._finderBubbleOpacity = Math.Clamp(dto.FinderBubbleOpacity, 0.04, 0.3);
                settings._showFinderBubble = dto.ShowFinderBubble;
                settings._finderHotkeyEnabled = dto.FinderHotkeyEnabled;
                settings._discordButtonEnabled = dto.DiscordButtonEnabled;
                settings._musicButtonEnabled = dto.MusicButtonEnabled;
                settings._musicShowVolume = dto.MusicShowVolume;
                settings._musicShowSourceToggle = dto.MusicShowSourceToggle;
                settings._musicPanelTheme = NormalizePanelTheme(dto.MusicPanelTheme, "Dark");
                settings._runCatPanelTheme = NormalizePanelTheme(dto.RunCatPanelTheme, "Dark");
                settings._topBarStyle = normalizedTopBarStyle;
                settings._solidColor = NormalizeHexColor(dto.SolidColor);
                settings._topBarForegroundColor = NormalizeHexColor(dto.TopBarForegroundColor);
                settings._blurIntensity = normalizedBlurIntensity;
                settings._runCatEnabled = dto.RunCatEnabled;
                settings._runCatRunner = dto.RunCatRunner is "Cat" or "Parrot" or "Horse" ? dto.RunCatRunner : "Cat";
                settings._topBarDisplayMode = NormalizeTopBarDisplayMode(dto.TopBarDisplayMode);
                settings._topBarMonitorIds = NormalizeTopBarMonitorIds(dto.TopBarMonitorIds);
                settings._showAppButtonOutline = dto.ShowAppButtonOutline;
                settings._localSpeechModelId = NormalizeLocalSpeechModelId(dto.LocalSpeechModelId);
                settings._gameDetectionMode = GameDetectionService.NormalizeDetectionMode(dto.GameDetectionMode);
                settings._gameProcessNames = NormalizeGameProcessNames(dto.GameProcessNames);
                settings._backgroundOptimizationEnabled = dto.BackgroundOptimizationEnabled;
                settings._systemPowerBoostEnabled = dto.SystemPowerBoostEnabled;
                settings._quietLaptopOutsideGamesEnabled = dto.QuietLaptopOutsideGamesEnabled;
                settings._hideForFullscreen = dto.HideForFullscreen;
                settings._shortcutButtons = NormalizeShortcutButtons(dto.ShortcutButtons);
                settings._terminalDefaultProfileId = dto.TerminalDefaultProfileId ?? string.Empty;
                settings._terminalFontFamily       = string.IsNullOrWhiteSpace(dto.TerminalFontFamily) ? "Cascadia Code, Consolas, Courier New, monospace" : dto.TerminalFontFamily;
                settings._terminalFontSize         = Math.Clamp(dto.TerminalFontSize, 8, 32);
                settings._terminalCursorStyle      = dto.TerminalCursorStyle is "block" or "underline" or "bar" ? dto.TerminalCursorStyle : "block";
                settings._terminalScrollback       = Math.Clamp(dto.TerminalScrollback, 100, 50000);
                settings._terminalCols             = Math.Clamp(dto.TerminalCols, 20, 500);
                settings._terminalRows             = Math.Clamp(dto.TerminalRows, 5, 200);

                if (requiresSave)
                {
                    settings.Save();
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to load app settings. Defaults will be used.", ex);
        }

        return settings;
    }

    private sealed class AppSettingsDto
    {
        public double TopBarOpacity { get; set; } = 0.92;
        public int ClockOffset { get; set; } = -10;
        public double ClockBalance { get; set; } = 0.35;
        public string TopBarContentAlignment { get; set; } = "Center";
        public double MenuTintOpacity { get; set; } = 0.14;
        public double FinderBubbleOpacity { get; set; } = 0.09;
        public bool ShowFinderBubble { get; set; } = true;
        public bool FinderHotkeyEnabled { get; set; } = true;
        public bool DiscordButtonEnabled { get; set; } = true;
        public bool MusicButtonEnabled { get; set; } = true;
        public bool MusicShowVolume { get; set; } = true;
        public bool MusicShowSourceToggle { get; set; } = true;
        public string MusicPanelTheme { get; set; } = "Dark";
        public string RunCatPanelTheme { get; set; } = "Dark";
        public string TopBarStyle { get; set; } = "Solid";
        public string SolidColor { get; set; } = "#000000";
        public string TopBarForegroundColor { get; set; } = "#FFFFFF";
        public double BlurIntensity { get; set; } = 0.15;
        public bool RunCatEnabled { get; set; }
        public string RunCatRunner { get; set; } = "Cat";
        public string TopBarDisplayMode { get; set; } = "Primary";
        public string[]? TopBarMonitorIds { get; set; }
        public bool ShowAppButtonOutline { get; set; } = true;
        public string LocalSpeechModelId { get; set; } = LocalSpeechModelCatalog.DefaultModelId;
        public string GameDetectionMode { get; set; } = GameDetectionService.HybridMode;
        public string[]? GameProcessNames { get; set; }
        public bool BackgroundOptimizationEnabled { get; set; } = true;
        public bool SystemPowerBoostEnabled { get; set; } = true;
        public bool QuietLaptopOutsideGamesEnabled { get; set; } = true;
        public bool HideForFullscreen { get; set; } = true;
        public AppShortcutDto?[]? ShortcutButtons { get; set; }
        public string TerminalDefaultProfileId { get; set; } = string.Empty;
        public string TerminalFontFamily       { get; set; } = "Cascadia Code, Consolas, Courier New, monospace";
        public int    TerminalFontSize         { get; set; } = 14;
        public string TerminalCursorStyle      { get; set; } = "block";
        public int    TerminalScrollback       { get; set; } = 5000;
        public int    TerminalCols             { get; set; } = 120;
        public int    TerminalRows             { get; set; } = 30;
    }

    private sealed class AppShortcutDto
    {
        public string Name { get; set; } = string.Empty;
        public string AppId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }
}
