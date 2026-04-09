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
            MenuTintOpacity = _menuTintOpacity,
            FinderBubbleOpacity = _finderBubbleOpacity,
            ShowFinderBubble = _showFinderBubble,
            FinderHotkeyEnabled = _finderHotkeyEnabled,
            DiscordButtonEnabled = _discordButtonEnabled,
            MusicButtonEnabled = _musicButtonEnabled,
            MusicShowVolume = _musicShowVolume,
            MusicShowSourceToggle = _musicShowSourceToggle,
            WeatherButtonEnabled = _weatherButtonEnabled,
            WeatherPrimaryCity = _weatherPrimaryCity,
            WeatherSecondaryCities = _weatherSecondaryCities,
            WeatherBlurIntensity = _weatherBlurIntensity,
            TopBarPanelTheme = _topBarPanelTheme,
            MusicPanelTheme = _musicPanelTheme,
            RunCatPanelTheme = _runCatPanelTheme,
            WeatherPanelTheme = _weatherPanelTheme,
            TopBarStyle = _topBarStyle,
            SolidColor = _solidColor,
            TopBarForegroundColor = _topBarForegroundColor,
            BlurIntensity = _blurIntensity,
            RunCatEnabled = _runCatEnabled,
            RunCatRunner = _runCatRunner,
            TopBarDisplayMode = _topBarDisplayMode,
            TopBarMonitorIds = _topBarMonitorIds,
            ShowAppButtonOutline = _showAppButtonOutline,
            AiProvider = _aiProvider,
            ChatGptAuthFilePath = _chatGptAuthFilePath,
            ChatGptModel = _chatGptModel,
            OpenAiBaseUrl = _openAiBaseUrl,
            OpenAiModel = _openAiModel,
            AnthropicBaseUrl = _anthropicBaseUrl,
            AnthropicModel = _anthropicModel,
            MistralBaseUrl = _mistralBaseUrl,
            MistralModel = _mistralModel,
            OllamaBaseUrl = _ollamaBaseUrl,
            OllamaModel = _ollamaModel,
            OllamaCloudBaseUrl = _ollamaCloudBaseUrl,
            OllamaCloudModel = _ollamaCloudModel,
            LocalSpeechModelId = _localSpeechModelId,
            GameDetectionMode = _gameDetectionMode,
            GameProcessNames = _gameProcessNames,
            BackgroundOptimizationEnabled = _backgroundOptimizationEnabled,
            SystemPowerBoostEnabled = _systemPowerBoostEnabled,
            ShortcutButtons = _shortcutButtons
                .Select(setting => setting is null
                    ? null
                    : new AppShortcutDto
                    {
                        Name = setting.AppName,
                        AppId = setting.AppId,
                        DisplayName = setting.DisplayName
                    })
                .ToArray()
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
                settings._menuTintOpacity = Math.Clamp(dto.MenuTintOpacity, 0.04, 0.3);
                settings._finderBubbleOpacity = Math.Clamp(dto.FinderBubbleOpacity, 0.04, 0.3);
                settings._showFinderBubble = dto.ShowFinderBubble;
                settings._finderHotkeyEnabled = dto.FinderHotkeyEnabled;
                settings._discordButtonEnabled = dto.DiscordButtonEnabled;
                settings._musicButtonEnabled = dto.MusicButtonEnabled;
                settings._musicShowVolume = dto.MusicShowVolume;
                settings._musicShowSourceToggle = dto.MusicShowSourceToggle;
                settings._weatherButtonEnabled = dto.WeatherButtonEnabled;
                settings._weatherPrimaryCity = string.IsNullOrWhiteSpace(dto.WeatherPrimaryCity) ? "Paris" : dto.WeatherPrimaryCity.Trim();
                settings._weatherSecondaryCities = NormalizeWeatherCities(dto.WeatherSecondaryCities);
                settings._weatherBlurIntensity = Math.Clamp(dto.WeatherBlurIntensity <= 0 ? 0.68 : dto.WeatherBlurIntensity, 0.2, 1.0);
                settings._topBarPanelTheme = NormalizePanelTheme(dto.TopBarPanelTheme, InferGlobalPanelTheme(dto));
                settings._musicPanelTheme = NormalizePanelTheme(dto.MusicPanelTheme, "Dark");
                settings._runCatPanelTheme = NormalizePanelTheme(dto.RunCatPanelTheme, "Dark");
                settings._weatherPanelTheme = NormalizePanelTheme(dto.WeatherPanelTheme, "Light");
                settings._topBarStyle = normalizedTopBarStyle;
                settings._solidColor = NormalizeHexColor(dto.SolidColor);
                settings._topBarForegroundColor = NormalizeHexColor(dto.TopBarForegroundColor);
                settings._blurIntensity = normalizedBlurIntensity;
                settings._runCatEnabled = dto.RunCatEnabled;
                settings._runCatRunner = dto.RunCatRunner is "Cat" or "Parrot" or "Horse" ? dto.RunCatRunner : "Cat";
                settings._topBarDisplayMode = NormalizeTopBarDisplayMode(dto.TopBarDisplayMode);
                settings._topBarMonitorIds = NormalizeTopBarMonitorIds(dto.TopBarMonitorIds);
                settings._showAppButtonOutline = dto.ShowAppButtonOutline;
                settings._aiProvider = NormalizeAiProvider(dto.AiProvider);
                settings._chatGptAuthFilePath = NormalizeAiSettingText(dto.ChatGptAuthFilePath, string.Empty);
                settings._chatGptModel = NormalizeAiSettingText(dto.ChatGptModel, "gpt-5.4");
                settings._openAiBaseUrl = NormalizeAiSettingText(dto.OpenAiBaseUrl, "https://api.openai.com/v1");
                settings._openAiModel = NormalizeAiSettingText(dto.OpenAiModel, "gpt-5.4");
                settings._anthropicBaseUrl = NormalizeAiSettingText(dto.AnthropicBaseUrl, "https://api.anthropic.com");
                settings._anthropicModel = NormalizeAiSettingText(dto.AnthropicModel, "claude-sonnet-4-20250514");
                settings._mistralBaseUrl = NormalizeAiSettingText(dto.MistralBaseUrl, "https://api.mistral.ai/v1");
                settings._mistralModel = NormalizeAiSettingText(dto.MistralModel, "mistral-large-latest");
                settings._ollamaBaseUrl = NormalizeAiSettingText(dto.OllamaBaseUrl, "http://127.0.0.1:11434/api");
                settings._ollamaModel = NormalizeAiSettingText(dto.OllamaModel, "qwen3-coder");
                settings._ollamaCloudBaseUrl = NormalizeAiSettingText(dto.OllamaCloudBaseUrl, "https://ollama.com/api");
                settings._ollamaCloudModel = NormalizeAiSettingText(dto.OllamaCloudModel, "gpt-oss:120b");
                settings._localSpeechModelId = NormalizeLocalSpeechModelId(dto.LocalSpeechModelId);
                settings._gameDetectionMode = GameDetectionService.NormalizeDetectionMode(dto.GameDetectionMode);
                settings._gameProcessNames = NormalizeGameProcessNames(dto.GameProcessNames);
                settings._backgroundOptimizationEnabled = dto.BackgroundOptimizationEnabled;
                settings._systemPowerBoostEnabled = dto.SystemPowerBoostEnabled;
                settings._shortcutButtons = NormalizeShortcutButtons(dto.ShortcutButtons);

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
        public double MenuTintOpacity { get; set; } = 0.14;
        public double FinderBubbleOpacity { get; set; } = 0.09;
        public bool ShowFinderBubble { get; set; } = true;
        public bool FinderHotkeyEnabled { get; set; } = true;
        public bool DiscordButtonEnabled { get; set; } = true;
        public bool MusicButtonEnabled { get; set; } = true;
        public bool MusicShowVolume { get; set; } = true;
        public bool MusicShowSourceToggle { get; set; } = true;
        public bool WeatherButtonEnabled { get; set; } = true;
        public string WeatherPrimaryCity { get; set; } = "Paris";
        public string[]? WeatherSecondaryCities { get; set; }
        public double WeatherBlurIntensity { get; set; } = 0.68;
        public string TopBarPanelTheme { get; set; } = "Dark";
        public string MusicPanelTheme { get; set; } = "Dark";
        public string RunCatPanelTheme { get; set; } = "Dark";
        public string WeatherPanelTheme { get; set; } = "Light";
        public string TopBarStyle { get; set; } = "Solid";
        public string SolidColor { get; set; } = "#000000";
        public string TopBarForegroundColor { get; set; } = "#FFFFFF";
        public double BlurIntensity { get; set; } = 0.15;
        public bool RunCatEnabled { get; set; }
        public string RunCatRunner { get; set; } = "Cat";
        public string TopBarDisplayMode { get; set; } = "Primary";
        public string[]? TopBarMonitorIds { get; set; }
        public bool ShowAppButtonOutline { get; set; } = true;
        public string AiProvider { get; set; } = AiProviderKind.ChatGptPremium;
        public string ChatGptAuthFilePath { get; set; } = string.Empty;
        public string ChatGptModel { get; set; } = "gpt-5.4";
        public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";
        public string OpenAiModel { get; set; } = "gpt-5.4";
        public string AnthropicBaseUrl { get; set; } = "https://api.anthropic.com";
        public string AnthropicModel { get; set; } = "claude-sonnet-4-20250514";
        public string MistralBaseUrl { get; set; } = "https://api.mistral.ai/v1";
        public string MistralModel { get; set; } = "mistral-large-latest";
        public string OllamaBaseUrl { get; set; } = "http://127.0.0.1:11434/api";
        public string OllamaModel { get; set; } = "qwen3-coder";
        public string OllamaCloudBaseUrl { get; set; } = "https://ollama.com/api";
        public string OllamaCloudModel { get; set; } = "gpt-oss:120b";
        public string LocalSpeechModelId { get; set; } = LocalSpeechModelCatalog.DefaultModelId;
        public string GameDetectionMode { get; set; } = GameDetectionService.HybridMode;
        public string[]? GameProcessNames { get; set; }
        public bool BackgroundOptimizationEnabled { get; set; } = true;
        public bool SystemPowerBoostEnabled { get; set; } = true;
        public AppShortcutDto?[]? ShortcutButtons { get; set; }
    }

    private sealed class AppShortcutDto
    {
        public string Name { get; set; } = string.Empty;
        public string AppId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }
}
