using System.Text.Json;
using System.Threading;
using Veil.Diagnostics;
using Veil.Interop;
using Veil.Services;

namespace Veil.Configuration;

internal sealed record AppShortcutSetting(string AppName, string AppId, string DisplayName);
internal sealed record DesktopWidgetSetting(
    string Id,
    string Kind,
    string Title,
    string MonitorId,
    int X,
    int Y,
    int Width,
    int Height,
    double Opacity,
    int CornerRadius,
    double Scale,
    string Theme,
    string BackgroundColor,
    string ForegroundColor,
    string AccentColor,
    int RefreshSeconds,
    bool ShowSeconds);

internal sealed partial class AppSettings
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
    private bool _desktopWidgetSnapToGrid = true;
    private int _desktopWidgetGridSize = 16;
    private string _topBarDisplayMode = "Primary";
    private string[] _topBarMonitorIds = [];
    private AppShortcutSetting?[] _shortcutButtons = new AppShortcutSetting?[MaxShortcutButtons];
    private DesktopWidgetSetting[] _desktopWidgets = [];

    public static AppSettings Current => _current.Value;

    public bool IsFirstLaunch => _isFirstLaunch;

    public event Action? Changed;

    private AppSettings(string settingsPath)
    {
        _settingsPath = settingsPath;
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
}
