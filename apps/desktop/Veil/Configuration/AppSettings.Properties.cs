using Veil.Interop;
using Veil.Services;

namespace Veil.Configuration;

internal sealed partial class AppSettings
{
    public double TopBarOpacity
    {
        get => _topBarOpacity;
        set
        {
            value = NormalizeTopBarOpacityForStyle(value, _topBarStyle, useDefaultWhenInvisible: false);
            if (Math.Abs(_topBarOpacity - value) < 0.001)
            {
                return;
            }

            _topBarOpacity = value;
            PersistAndNotify();
        }
    }

    public int ClockOffset
    {
        get => _clockOffset;
        set
        {
            value = Math.Clamp(value, -40, 40);
            if (_clockOffset == value)
            {
                return;
            }

            _clockOffset = value;
            PersistAndNotify();
        }
    }

    public double ClockBalance
    {
        get => _clockBalance;
        set
        {
            value = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(_clockBalance - value) < 0.001)
            {
                return;
            }

            _clockBalance = value;
            PersistAndNotify();
        }
    }

    public double MenuTintOpacity
    {
        get => _menuTintOpacity;
        set
        {
            value = Math.Clamp(value, 0.04, 0.3);
            if (Math.Abs(_menuTintOpacity - value) < 0.001)
            {
                return;
            }

            _menuTintOpacity = value;
            PersistAndNotify();
        }
    }

    public double FinderBubbleOpacity
    {
        get => _finderBubbleOpacity;
        set
        {
            value = Math.Clamp(value, 0.04, 0.3);
            if (Math.Abs(_finderBubbleOpacity - value) < 0.001)
            {
                return;
            }

            _finderBubbleOpacity = value;
            PersistAndNotify();
        }
    }

    public bool ShowFinderBubble
    {
        get => _showFinderBubble;
        set
        {
            if (_showFinderBubble == value)
            {
                return;
            }

            _showFinderBubble = value;
            PersistAndNotify();
        }
    }

    public bool FinderHotkeyEnabled
    {
        get => _finderHotkeyEnabled;
        set
        {
            if (_finderHotkeyEnabled == value)
            {
                return;
            }

            _finderHotkeyEnabled = value;
            PersistAndNotify();
        }
    }

    public string TopBarStyle
    {
        get => _topBarStyle;
        set
        {
            value = NormalizeTopBarStyle(value);
            double normalizedOpacity = NormalizeTopBarOpacityForStyle(_topBarOpacity, value, useDefaultWhenInvisible: true);
            double normalizedBlurIntensity = NormalizeBlurIntensityForStyle(_blurIntensity, value, useDefaultWhenInvisible: true);

            if (_topBarStyle == value
                && Math.Abs(_topBarOpacity - normalizedOpacity) < 0.001
                && Math.Abs(_blurIntensity - normalizedBlurIntensity) < 0.001)
            {
                return;
            }

            _topBarStyle = value;
            _topBarOpacity = normalizedOpacity;
            _blurIntensity = normalizedBlurIntensity;
            PersistAndNotify();
        }
    }

    public string MusicPanelTheme
    {
        get => _musicPanelTheme;
        set
        {
            value = NormalizePanelTheme(value);
            if (_musicPanelTheme == value)
            {
                return;
            }

            _musicPanelTheme = value;
            PersistAndNotify();
        }
    }

    public string RunCatPanelTheme
    {
        get => _runCatPanelTheme;
        set
        {
            value = NormalizePanelTheme(value);
            if (_runCatPanelTheme == value)
            {
                return;
            }

            _runCatPanelTheme = value;
            PersistAndNotify();
        }
    }

    public bool DiscordButtonEnabled
    {
        get => _discordButtonEnabled;
        set
        {
            if (_discordButtonEnabled == value)
            {
                return;
            }

            _discordButtonEnabled = value;
            PersistAndNotify();
        }
    }

    public bool MusicButtonEnabled
    {
        get => _musicButtonEnabled;
        set
        {
            if (_musicButtonEnabled == value)
            {
                return;
            }

            _musicButtonEnabled = value;
            PersistAndNotify();
        }
    }

    public bool MusicShowVolume
    {
        get => _musicShowVolume;
        set
        {
            if (_musicShowVolume == value)
            {
                return;
            }

            _musicShowVolume = value;
            PersistAndNotify();
        }
    }

    public bool MusicShowSourceToggle
    {
        get => _musicShowSourceToggle;
        set
        {
            if (_musicShowSourceToggle == value)
            {
                return;
            }

            _musicShowSourceToggle = value;
            PersistAndNotify();
        }
    }

    public string SolidColor
    {
        get => _solidColor;
        set
        {
            value = NormalizeHexColor(value);
            if (string.Equals(_solidColor, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _solidColor = value;
            PersistAndNotify();
        }
    }

    public string TopBarForegroundColor
    {
        get => _topBarForegroundColor;
        set
        {
            value = NormalizeHexColor(value);
            if (string.Equals(_topBarForegroundColor, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _topBarForegroundColor = value;
            PersistAndNotify();
        }
    }

    public double BlurIntensity
    {
        get => _blurIntensity;
        set
        {
            value = NormalizeBlurIntensityForStyle(value, _topBarStyle, useDefaultWhenInvisible: false);
            if (Math.Abs(_blurIntensity - value) < 0.001)
            {
                return;
            }

            _blurIntensity = value;
            PersistAndNotify();
        }
    }

    public bool RunCatEnabled
    {
        get => _runCatEnabled;
        set
        {
            if (_runCatEnabled == value)
            {
                return;
            }

            _runCatEnabled = value;
            PersistAndNotify();
        }
    }

    public string RunCatRunner
    {
        get => _runCatRunner;
        set
        {
            value = value is "Cat" or "Parrot" or "Horse" ? value : "Cat";
            if (_runCatRunner == value)
            {
                return;
            }

            _runCatRunner = value;
            PersistAndNotify();
        }
    }

    public IReadOnlyList<string> GameProcessNames => _gameProcessNames;

    public string TopBarDisplayMode
    {
        get => _topBarDisplayMode;
        set
        {
            value = NormalizeTopBarDisplayMode(value);
            if (_topBarDisplayMode == value)
            {
                return;
            }

            _topBarDisplayMode = value;
            PersistAndNotify();
        }
    }

    public IReadOnlyList<string> TopBarMonitorIds => _topBarMonitorIds;

    public bool ShowAppButtonOutline
    {
        get => _showAppButtonOutline;
        set
        {
            if (_showAppButtonOutline == value)
            {
                return;
            }

            _showAppButtonOutline = value;
            PersistAndNotify();
        }
    }

    public string AiProvider
    {
        get => _aiProvider;
        set
        {
            value = NormalizeAiProvider(value);
            if (_aiProvider == value)
            {
                return;
            }

            _aiProvider = value;
            PersistAndNotify();
        }
    }

    public string ChatGptAuthFilePath
    {
        get => _chatGptAuthFilePath;
        set
        {
            value = NormalizeAiSettingText(value, string.Empty);
            if (_chatGptAuthFilePath == value)
            {
                return;
            }

            _chatGptAuthFilePath = value;
            PersistAndNotify();
        }
    }

    public string ChatGptModel
    {
        get => _chatGptModel;
        set
        {
            value = NormalizeAiSettingText(value, "gpt-5.4");
            if (_chatGptModel == value)
            {
                return;
            }

            _chatGptModel = value;
            PersistAndNotify();
        }
    }

    public string OpenAiBaseUrl
    {
        get => _openAiBaseUrl;
        set
        {
            value = NormalizeAiSettingText(value, "https://api.openai.com/v1");
            if (_openAiBaseUrl == value)
            {
                return;
            }

            _openAiBaseUrl = value;
            PersistAndNotify();
        }
    }

    public string OpenAiModel
    {
        get => _openAiModel;
        set
        {
            value = NormalizeAiSettingText(value, "gpt-5.4");
            if (_openAiModel == value)
            {
                return;
            }

            _openAiModel = value;
            PersistAndNotify();
        }
    }

    public string AnthropicBaseUrl
    {
        get => _anthropicBaseUrl;
        set
        {
            value = NormalizeAiSettingText(value, "https://api.anthropic.com");
            if (_anthropicBaseUrl == value)
            {
                return;
            }

            _anthropicBaseUrl = value;
            PersistAndNotify();
        }
    }

    public string AnthropicModel
    {
        get => _anthropicModel;
        set
        {
            value = NormalizeAiSettingText(value, "claude-sonnet-4-20250514");
            if (_anthropicModel == value)
            {
                return;
            }

            _anthropicModel = value;
            PersistAndNotify();
        }
    }

    public string MistralBaseUrl
    {
        get => _mistralBaseUrl;
        set
        {
            value = NormalizeAiSettingText(value, "https://api.mistral.ai/v1");
            if (_mistralBaseUrl == value)
            {
                return;
            }

            _mistralBaseUrl = value;
            PersistAndNotify();
        }
    }

    public string MistralModel
    {
        get => _mistralModel;
        set
        {
            value = NormalizeAiSettingText(value, "mistral-large-latest");
            if (_mistralModel == value)
            {
                return;
            }

            _mistralModel = value;
            PersistAndNotify();
        }
    }

    public string OllamaBaseUrl
    {
        get => _ollamaBaseUrl;
        set
        {
            value = NormalizeAiSettingText(value, "http://127.0.0.1:11434/api");
            if (_ollamaBaseUrl == value)
            {
                return;
            }

            _ollamaBaseUrl = value;
            PersistAndNotify();
        }
    }

    public string OllamaModel
    {
        get => _ollamaModel;
        set
        {
            value = NormalizeAiSettingText(value, "qwen3-coder");
            if (_ollamaModel == value)
            {
                return;
            }

            _ollamaModel = value;
            PersistAndNotify();
        }
    }

    public string OllamaCloudBaseUrl
    {
        get => _ollamaCloudBaseUrl;
        set
        {
            value = NormalizeAiSettingText(value, "https://ollama.com/api");
            if (_ollamaCloudBaseUrl == value)
            {
                return;
            }

            _ollamaCloudBaseUrl = value;
            PersistAndNotify();
        }
    }

    public string OllamaCloudModel
    {
        get => _ollamaCloudModel;
        set
        {
            value = NormalizeAiSettingText(value, "gpt-oss:120b");
            if (_ollamaCloudModel == value)
            {
                return;
            }

            _ollamaCloudModel = value;
            PersistAndNotify();
        }
    }

    public string LocalSpeechModelId
    {
        get => _localSpeechModelId;
        set
        {
            value = NormalizeLocalSpeechModelId(value);
            if (_localSpeechModelId == value)
            {
                return;
            }

            _localSpeechModelId = value;
            PersistAndNotify();
        }
    }

    public string GameDetectionMode
    {
        get => _gameDetectionMode;
        set
        {
            value = GameDetectionService.NormalizeDetectionMode(value);
            if (_gameDetectionMode == value)
            {
                return;
            }

            _gameDetectionMode = value;
            PersistAndNotify();
        }
    }

    public void SetGameProcessNames(IEnumerable<string> processNames)
    {
        string[] normalizedNames = processNames
            .Select(static name => name.Trim())
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(GameProcessMonitor.NormalizeProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (_gameProcessNames.SequenceEqual(normalizedNames, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _gameProcessNames = normalizedNames;
        PersistAndNotify();
    }

    public bool BackgroundOptimizationEnabled
    {
        get => _backgroundOptimizationEnabled;
        set
        {
            if (_backgroundOptimizationEnabled == value)
            {
                return;
            }

            _backgroundOptimizationEnabled = value;
            PersistAndNotify();
        }
    }

    public bool SystemPowerBoostEnabled
    {
        get => _systemPowerBoostEnabled;
        set
        {
            if (_systemPowerBoostEnabled == value)
            {
                return;
            }

            _systemPowerBoostEnabled = value;
            PersistAndNotify();
        }
    }

    public bool QuietLaptopOutsideGamesEnabled
    {
        get => _quietLaptopOutsideGamesEnabled;
        set
        {
            if (_quietLaptopOutsideGamesEnabled == value)
            {
                return;
            }

            _quietLaptopOutsideGamesEnabled = value;
            PersistAndNotify();
        }
    }

    public void SetTopBarMonitorIds(IEnumerable<string> monitorIds)
    {
        string[] normalizedIds = monitorIds
            .Select(static id => id.Trim())
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (_topBarMonitorIds.SequenceEqual(normalizedIds, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _topBarMonitorIds = normalizedIds;
        PersistAndNotify();
    }

    public IReadOnlyList<string> ResolveTopBarMonitorIds(IReadOnlyList<MonitorInfo2> monitors)
    {
        if (monitors.Count == 0)
        {
            return [];
        }

        return _topBarDisplayMode switch
        {
            "All" => monitors.Select(static monitor => monitor.Id).ToArray(),
            "Custom" => ResolveCustomTopBarMonitorIds(monitors),
            _ => [GetPrimaryMonitorId(monitors)]
        };
    }

    public IReadOnlyList<AppShortcutSetting?> ShortcutButtons => _shortcutButtons;

    public void SetShortcutButton(int index, AppShortcutSetting? setting)
    {
        if (index < 0 || index >= MaxShortcutButtons)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (Equals(_shortcutButtons[index], setting))
        {
            return;
        }

        _shortcutButtons[index] = setting;
        PersistAndNotify();
    }
}
