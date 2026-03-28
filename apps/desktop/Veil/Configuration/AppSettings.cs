using System.Text.Json;
using System.Threading;
using Veil.Diagnostics;
using Veil.Interop;
using Veil.Services;

namespace Veil.Configuration;

internal sealed record AppShortcutSetting(string AppName, string AppId, string DisplayName);

internal sealed class AppSettings
{
    internal const int MaxShortcutButtons = 4;
    private const double DefaultTopBarOpacity = 0.92;
    private const double MinimumVisibleTopBarOpacity = 0.04;
    private const double DefaultBlurIntensity = 0.15;
    private const double MinimumVisibleBlurIntensity = 0.05;
    private static readonly Lazy<AppSettings> _current = new(LoadOrCreate);
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    private const int SaveDebounceMilliseconds = 300;

    private readonly string _settingsPath;
    private Timer? _saveTimer;
    private bool _isFirstLaunch;
    private double _topBarOpacity = DefaultTopBarOpacity;
    private int _clockOffset = -10;
    private double _clockBalance = 0.35;
    private double _menuTintOpacity = 0.14;
    private double _finderBubbleOpacity = 0.09;
    private bool _showFinderBubble = true;
    private bool _finderHotkeyEnabled = true;
    private bool _discordButtonEnabled = true;
    private bool _musicButtonEnabled = true;
    private bool _musicShowVolume = true;
    private bool _musicShowSourceToggle = true;
    private bool _weatherButtonEnabled = true;
    private string _weatherPrimaryCity = "Paris";
    private string[] _weatherSecondaryCities =
    [
        "Cupertino",
        "New York",
        "London",
        "Beijing"
    ];
    private double _weatherBlurIntensity = 0.68;
    private string _topBarPanelTheme = "Dark";
    private string _musicPanelTheme = "Dark";
    private string _runCatPanelTheme = "Dark";
    private string _weatherPanelTheme = "Light";
    private string _topBarStyle = "Solid";
    private string _solidColor = "#000000";
    private string _topBarForegroundColor = "#FFFFFF";
    private double _blurIntensity = DefaultBlurIntensity;
    private bool _runCatEnabled;
    private string _runCatRunner = "Cat";
    private string _gameDetectionMode = GameDetectionService.HybridMode;
    private string[] _gameProcessNames = [];
    private bool _backgroundOptimizationEnabled = true;
    private string _topBarDisplayMode = "Primary";
    private string[] _topBarMonitorIds = [];
    private AppShortcutSetting?[] _shortcutButtons = new AppShortcutSetting?[MaxShortcutButtons];

    public static AppSettings Current => _current.Value;

    public bool IsFirstLaunch => _isFirstLaunch;

    public event Action? Changed;

    private AppSettings(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

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
            GameDetectionMode = _gameDetectionMode,
            GameProcessNames = _gameProcessNames,
            BackgroundOptimizationEnabled = _backgroundOptimizationEnabled,
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

    private void PersistAndNotify()
    {
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ =>
        {
            try
            {
                Save();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to save app settings.", ex);
            }
        }, null, SaveDebounceMilliseconds, Timeout.Infinite);

        Changed?.Invoke();
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
                settings._gameDetectionMode = GameDetectionService.NormalizeDetectionMode(dto.GameDetectionMode);
                settings._gameProcessNames = NormalizeGameProcessNames(dto.GameProcessNames);
                settings._backgroundOptimizationEnabled = dto.BackgroundOptimizationEnabled;
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
        public string GameDetectionMode { get; set; } = GameDetectionService.HybridMode;
        public string[]? GameProcessNames { get; set; }
        public bool BackgroundOptimizationEnabled { get; set; } = true;
        public AppShortcutDto?[]? ShortcutButtons { get; set; }
    }

    private sealed class AppShortcutDto
    {
        public string Name { get; set; } = string.Empty;
        public string AppId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }
}
