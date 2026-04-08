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

    public string TopBarPanelTheme
    {
        get => _topBarPanelTheme;
        set
        {
            value = NormalizePanelTheme(value);
            if (_topBarPanelTheme == value)
            {
                return;
            }

            _topBarPanelTheme = value;
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

    public string WeatherPanelTheme
    {
        get => _weatherPanelTheme;
        set
        {
            value = NormalizePanelTheme(value);
            if (_weatherPanelTheme == value)
            {
                return;
            }

            _weatherPanelTheme = value;
            PersistAndNotify();
        }
    }

    public bool WeatherButtonEnabled
    {
        get => _weatherButtonEnabled;
        set
        {
            if (_weatherButtonEnabled == value)
            {
                return;
            }

            _weatherButtonEnabled = value;
            PersistAndNotify();
        }
    }

    public string WeatherPrimaryCity
    {
        get => _weatherPrimaryCity;
        set
        {
            value = string.IsNullOrWhiteSpace(value) ? "Paris" : value.Trim();
            if (string.Equals(_weatherPrimaryCity, value, StringComparison.Ordinal))
            {
                return;
            }

            _weatherPrimaryCity = value;
            PersistAndNotify();
        }
    }

    public IReadOnlyList<string> WeatherSecondaryCities => _weatherSecondaryCities;

    public void SetWeatherSecondaryCities(IEnumerable<string> cityNames)
    {
        string[] normalizedCities = cityNames
            .Select(static name => name.Trim())
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

        if (_weatherSecondaryCities.SequenceEqual(normalizedCities, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _weatherSecondaryCities = normalizedCities;
        PersistAndNotify();
    }

    public double WeatherBlurIntensity
    {
        get => _weatherBlurIntensity;
        set
        {
            value = Math.Clamp(value, 0.2, 1.0);
            if (Math.Abs(_weatherBlurIntensity - value) < 0.001)
            {
                return;
            }

            _weatherBlurIntensity = value;
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
