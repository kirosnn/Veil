using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Veil.Configuration;
using Veil.Diagnostics;
using Veil.Interop;
using Veil.Services;
using static Veil.Interop.NativeMethods;

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

    internal void OpenTerminalWindow()
    {
        try
        {
            if (_terminalWindow is null)
            {
                _terminalWindow = new TerminalWindow(this);
                _terminalWindow.Closed += OnTerminalWindowClosed;
                _terminalWindow.Activate();
            }
            else
            {
                var appWindow = WindowHelper.GetAppWindow(_terminalWindow);
                appWindow?.Show();
                SetForegroundWindow(WindowHelper.GetHwnd(_terminalWindow));
                _terminalWindow.EnsureDefaultTab();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to open terminal window.", ex);
        }
    }

    private void OnTerminalWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is TerminalWindow window)
        {
            window.Closed -= OnTerminalWindowClosed;
            if (ReferenceEquals(_terminalWindow, window))
            {
                _terminalWindow = null;
            }
        }
    }

    // ── Terminal tab strip (in TopBar) ──────────────────────────────────────

    private readonly Dictionary<int, Button> _terminalTabButtons = [];
    private int _activeTerminalTabId = -1;

    internal void OnTerminalTabAdded(int tabId, string title)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var closeBtn = new Button
            {
                Content = "×",
                Width = 14,
                Height = 14,
                Padding = new Thickness(0),
                Margin = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 255, 255, 255)),
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(130, 255, 255, 255)),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                IsTabStop = false,
                Tag = tabId
            };
            closeBtn.Click += OnTerminalTabCloseClick;
            closeBtn.PointerEntered += (_, _) => closeBtn.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(40, 255, 255, 255));
            closeBtn.PointerExited += (_, _) => closeBtn.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 255, 255, 255));

            var titleText = new TextBlock
            {
                Text = TruncateTerminalTitle(title),
                FontSize = 11,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(180, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 110,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 2,
                Children = { titleText, closeBtn }
            };

            var tabBtn = new Button
            {
                Content = content,
                Height = 22,
                Padding = new Thickness(8, 0, 4, 0),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center,
                IsTabStop = false,
                Tag = tabId
            };
            tabBtn.Click += OnTerminalTabButtonClick;
            tabBtn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(18, 255, 255, 255));
            tabBtn.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(10, 255, 255, 255));
            tabBtn.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 255, 255, 255));

            _terminalTabButtons[tabId] = tabBtn;
            TerminalTabStrip.Children.Add(tabBtn);
            TerminalTabPanel.Visibility = Visibility.Visible;
            ClockText.Visibility = Visibility.Collapsed;
            _lastWindowRegionSignature = string.Empty;
            UpdateWindowRegion();
        });
    }

    internal void OnTerminalTabClosed(int tabId)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_terminalTabButtons.TryGetValue(tabId, out var btn))
            {
                TerminalTabStrip.Children.Remove(btn);
                _terminalTabButtons.Remove(tabId);
            }

            if (_terminalTabButtons.Count == 0)
            {
                TerminalTabPanel.Visibility = Visibility.Collapsed;
                ClockText.Visibility = Visibility.Visible;
                _activeTerminalTabId = -1;
                _lastWindowRegionSignature = string.Empty;
                UpdateWindowRegion();
            }
        });
    }

    internal void OnTerminalTabActivated(int tabId)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _activeTerminalTabId = tabId;
            foreach (var (id, btn) in _terminalTabButtons)
            {
                bool isActive = id == tabId;
                btn.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(isActive ? (byte)28 : (byte)0, 255, 255, 255));
                if (btn.Content is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock tb)
                {
                    tb.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(isActive ? (byte)230 : (byte)160, 255, 255, 255));
                }
            }
        });
    }

    internal void OnTerminalTabTitleChanged(int tabId, string title)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_terminalTabButtons.TryGetValue(tabId, out var btn)) return;
            if (btn.Content is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock tb)
            {
                tb.Text = TruncateTerminalTitle(title);
            }
        });
    }

    internal void OnTerminalClosed()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TerminalTabStrip.Children.Clear();
            _terminalTabButtons.Clear();
            _activeTerminalTabId = -1;
            TerminalTabPanel.Visibility = Visibility.Collapsed;
            ClockText.Visibility = Visibility.Visible;
            _lastWindowRegionSignature = string.Empty;
            UpdateWindowRegion();
        });
    }

    private void OnTerminalTabButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int tabId } && _terminalWindow is not null)
        {
            var appWindow = WindowHelper.GetAppWindow(_terminalWindow);
            appWindow?.Show();
            SetForegroundWindow(WindowHelper.GetHwnd(_terminalWindow));
            _terminalWindow.ExternalActivateTab(tabId);
        }
    }

    private void OnTerminalTabCloseClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int tabId } && _terminalWindow is not null)
        {
            _terminalWindow.ExternalCloseTab(tabId);
        }
    }

    private void OnTerminalNewTabClick(object sender, RoutedEventArgs e)
    {
        if (_terminalWindow is null)
        {
            OpenTerminalWindow();
        }
        else
        {
            var appWindow = WindowHelper.GetAppWindow(_terminalWindow);
            appWindow?.Show();
            SetForegroundWindow(WindowHelper.GetHwnd(_terminalWindow));
            _terminalWindow.OnNewTabClick(null, null);
        }
    }

    private static string TruncateTerminalTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "Terminal";
        return title.Length > 20 ? title[..20] + "…" : title;
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
            InitializeDictationHotkey();
            _finderHotkeyService?.SetEnabled(_settings.FinderHotkeyEnabled);
            return;
        }

        DisposeFinderHotkey();
        DisposeDictationHotkey();
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
        _finderWindow.Closed += (_, _) => _finderWindow = null;
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

        if (_settings.RunCatEnabled)
        {
            EnsureSystemStatsWindowCreated();
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
        List<Button> shortcutButtons = [];

        foreach (var shortcut in _settings.ShortcutButtons.Where(setting => setting is not null).Cast<AppShortcutSetting>())
        {
            Button button = CreateShortcutButton(shortcut);
            shortcutButtons.Add(button);
            ShortcutButtonsPanel.Children.Add(button);
        }

        _shortcutButtons = shortcutButtons.ToArray();
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
