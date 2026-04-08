using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Veil.Configuration;
using Veil.Diagnostics;
using Veil.Interop;
using Veil.Services;

namespace Veil.Windows;

public sealed partial class TopBarWindow
{
    private void OnMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isGameMinimalMode)
        {
            return;
        }

        OpenSettingsWindow();
    }

    internal void OpenSettings() => OpenSettingsWindow();

    private void OpenSettingsWindow()
    {
        try
        {
            AppLogger.Info("Settings window open requested.");
            if (_settingsWindow != null)
            {
                _settingsWindow.Close();
            }

            _settingsWindow = new SettingsWindow(_screen, _monitorId);
            _settingsWindow.Closed += OnSettingsWindowClosed;
            _settingsWindow.ShowCentered();
            AppLogger.Info("Settings window show requested.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to open settings window.", ex);
        }
    }

    private void OnSettingsWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is SettingsWindow window)
        {
            window.Closed -= OnSettingsWindowClosed;
            if (ReferenceEquals(_settingsWindow, window))
            {
                _settingsWindow = null;
            }
        }
    }

    private void OnFinderButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isGameMinimalMode)
        {
            return;
        }

        OpenFinderWindow();
    }

    private void InitializeFinderHotkey()
    {
        if (_finderHotkeyService is not null)
        {
            return;
        }

        _finderHotkeyService = new FinderHotkeyService();
        _finderHotkeyService.Triggered += OnFinderHotkeyTriggered;
        _finderHotkeyService.Initialize();
        _finderHotkeyService.SetEnabled(_settings.FinderHotkeyEnabled);
    }

    private void DisposeFinderHotkey()
    {
        if (_finderHotkeyService is null)
        {
            return;
        }

        _finderHotkeyService.Triggered -= OnFinderHotkeyTriggered;
        _finderHotkeyService.Dispose();
        _finderHotkeyService = null;
    }

    private void ApplyHotkeyOwnership()
    {
        if (_ownsGlobalHotkeys)
        {
            InitializeFinderHotkey();
            _finderHotkeyService?.SetEnabled(_settings.FinderHotkeyEnabled);
            return;
        }

        DisposeFinderHotkey();
    }

    internal void SetOwnsGlobalHotkeys(bool ownsGlobalHotkeys)
    {
        if (_ownsGlobalHotkeys == ownsGlobalHotkeys)
        {
            return;
        }

        _ownsGlobalHotkeys = ownsGlobalHotkeys;
        ApplyHotkeyOwnership();
    }

    private void OnFinderHotkeyTriggered()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isGameMinimalMode)
            {
                return;
            }

            OpenFinderWindow();
        });
    }

    private void EnsureFinderWindowCreated()
    {
        if (_finderWindow != null)
        {
            return;
        }

        _finderWindow = new FinderWindow(_screen);
        _finderWindow.Prewarm();
    }

    private SystemStatsWindow EnsureSystemStatsWindowCreated()
    {
        if (_systemStatsWindow is not null)
        {
            return _systemStatsWindow;
        }

        _systemStatsWindow = new SystemStatsWindow();
        _systemStatsWindow.Initialize();
        return _systemStatsWindow;
    }

    private PanelThemeWindow EnsurePanelThemeWindowCreated()
    {
        if (_panelThemeWindow is not null)
        {
            return _panelThemeWindow;
        }

        _panelThemeWindow = new PanelThemeWindow();
        _panelThemeWindow.Initialize();
        return _panelThemeWindow;
    }

    private WeatherWindow EnsureWeatherWindowCreated()
    {
        if (_weatherWindow is not null)
        {
            return _weatherWindow;
        }

        _weatherWindow = new WeatherWindow();
        _weatherWindow.SetWeatherService(_weatherService);
        _weatherWindow.Initialize();
        return _weatherWindow;
    }

    private MusicControlWindow EnsureMusicControlWindowCreated()
    {
        if (_musicControlWindow is not null)
        {
            return _musicControlWindow;
        }

        _musicControlWindow = new MusicControlWindow();
        _musicControlWindow.SetMediaService(_mediaControlService);
        _musicControlWindow.Initialize();
        return _musicControlWindow;
    }

    private DiscordNotificationWindow EnsureDiscordNotificationWindowCreated()
    {
        if (_discordNotificationWindow is not null)
        {
            return _discordNotificationWindow;
        }

        _discordNotificationWindow = new DiscordNotificationWindow();
        _discordNotificationWindow.SetDiscordService(_discordNotificationService);
        _discordNotificationWindow.Initialize();
        return _discordNotificationWindow;
    }

    private void PrewarmTransientWindows()
    {
        EnsureFinderWindowCreated();
        EnsurePanelThemeWindowCreated();

        if (_settings.RunCatEnabled)
        {
            EnsureSystemStatsWindowCreated();
        }

        if (_settings.WeatherButtonEnabled)
        {
            EnsureWeatherWindowCreated();
        }

        if (_settings.MusicButtonEnabled)
        {
            EnsureMusicControlWindowCreated();
        }

        if (_settings.DiscordButtonEnabled)
        {
            EnsureDiscordNotificationWindowCreated();
        }
    }

    private void OpenFinderWindow()
    {
        try
        {
            EnsureFinderWindowCreated();
            _finderWindow!.ShowCentered();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to open finder window.", ex);
        }
    }

    private void OnRunCatButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isGameMinimalMode)
        {
            return;
        }

        if (_systemStatsWindow is { IsStatsVisible: true })
        {
            _systemStatsWindow.Hide();
            return;
        }

        SystemStatsWindow window = EnsureSystemStatsWindowCreated();

        if ((DateTime.UtcNow - window.LastHiddenAtUtc).TotalMilliseconds < 200)
        {
            return;
        }

        int barWidth = _screen.Right - _screen.Left;
        window.ShowAt(_screen.Left + barWidth - 6, _screen.Top + BarHeight);
    }

    private void OnPanelThemeButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isGameMinimalMode)
        {
            return;
        }

        if (_panelThemeWindow is { IsPanelVisible: true })
        {
            _panelThemeWindow.Hide();
            return;
        }

        PanelThemeWindow window = EnsurePanelThemeWindowCreated();

        if ((DateTime.UtcNow - window.LastHiddenAtUtc).TotalMilliseconds < 200)
        {
            return;
        }

        int barWidth = _screen.Right - _screen.Left;
        window.ShowAt(_screen.Left + barWidth - 6, _screen.Top + BarHeight);
    }

    private void OnWeatherButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isGameMinimalMode)
        {
            return;
        }

        if (_weatherWindow is { IsWeatherVisible: true })
        {
            _weatherWindow.Hide();
            return;
        }

        WeatherWindow window = EnsureWeatherWindowCreated();

        if ((DateTime.UtcNow - window.LastHiddenAtUtc).TotalMilliseconds < 200)
        {
            return;
        }

        int barWidth = _screen.Right - _screen.Left;
        window.ShowAt(_screen.Left + barWidth - 6, _screen.Top + BarHeight);
    }

    private void OnMusicButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isGameMinimalMode)
        {
            return;
        }

        if (_musicControlWindow is { IsMenuVisible: true })
        {
            _musicControlWindow.Hide();
            return;
        }

        MusicControlWindow window = EnsureMusicControlWindowCreated();

        if ((DateTime.UtcNow - window.LastHiddenAtUtc).TotalMilliseconds < 200)
        {
            return;
        }

        int barWidth = _screen.Right - _screen.Left;
        window.ShowAt(_screen.Left + barWidth - 6, _screen.Top + BarHeight);
    }

    private void OnDiscordButtonClick(object sender, RoutedEventArgs e)
    {
        UpdateDiscordDemand(boost: true);

        if (_isGameMinimalMode)
        {
            return;
        }

        if (_discordNotificationWindow is { IsMenuVisible: true })
        {
            _discordNotificationWindow.Hide();
            return;
        }

        DiscordNotificationWindow window = EnsureDiscordNotificationWindowCreated();

        if ((DateTime.UtcNow - window.LastHiddenAtUtc).TotalMilliseconds < 200)
        {
            return;
        }

        int barWidth = _screen.Right - _screen.Left;
        window.ShowAt(_screen.Left + barWidth - 6, _screen.Top + BarHeight);
    }

    private void RebuildShortcutButtons()
    {
        ShortcutButtonsPanel.Children.Clear();

        foreach (var shortcut in _settings.ShortcutButtons.Where(setting => setting is not null).Cast<AppShortcutSetting>())
        {
            ShortcutButtonsPanel.Children.Add(CreateShortcutButton(shortcut));
        }

        UpdateGlassPanels(true);
        UpdateTopBarLayout();
    }

    private Button CreateShortcutButton(AppShortcutSetting shortcut)
    {
        var button = new Button
        {
            MinWidth = 0,
            MaxWidth = 130,
            Padding = new Thickness(9, 1, 9, 1),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(
                _settings.ShowFinderBubble ? (byte)Math.Round(_settings.FinderBubbleOpacity * 215) : (byte)0,
                GetTopBarForegroundColor().R, GetTopBarForegroundColor().G, GetTopBarForegroundColor().B)),
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(230, GetTopBarForegroundColor().R, GetTopBarForegroundColor().G, GetTopBarForegroundColor().B)),
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            IsTabStop = false,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = shortcut
        };

        var foregroundColor = GetTopBarForegroundColor();
        button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(48, foregroundColor.R, foregroundColor.G, foregroundColor.B));
        button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(28, foregroundColor.R, foregroundColor.G, foregroundColor.B));
        button.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 255, 255, 255));
        button.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 255, 255, 255));
        button.Click += OnShortcutButtonClick;

        button.Content = new TextBlock
        {
            Text = shortcut.DisplayName,
            MaxWidth = 110,
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(230, foregroundColor.R, foregroundColor.G, foregroundColor.B))
        };

        return button;
    }

    private void OnShortcutButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppShortcutSetting shortcut })
        {
            return;
        }

        InstalledAppService.LaunchOrActivate(new InstalledApp(
            shortcut.AppName,
            shortcut.AppId,
            shortcut.AppName,
            shortcut.AppName));
    }
}
