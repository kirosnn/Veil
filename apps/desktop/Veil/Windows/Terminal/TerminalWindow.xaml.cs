using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Veil.Configuration;
using Veil.Diagnostics;
using Veil.Interop;
using Veil.Services.Terminal;
using WinRT;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class TerminalWindow : Window
{
    private static int s_nextTabId;

    private sealed class TerminalTab
    {
        public int TabId { get; }
        public string ProfileId => Profile.Id;
        public TerminalProfile Profile { get; }
        public string Title { get; set; }
        public TerminalSession? Session { get; set; }
        public NativeTerminalControl View { get; }
        public Button TabButton { get; set; } = null!;
        public bool IsClosed { get; set; }

        public TerminalTab(int tabId, TerminalProfile profile, NativeTerminalControl view)
        {
            TabId = tabId;
            Profile = profile;
            Title = profile.DisplayName;
            View = view;
        }
    }

    private readonly AppSettings _settings;
    private readonly TopBarWindow _topBar;
    private readonly Dictionary<int, TerminalTab> _tabs = [];
    private int _activeTabId = -1;
    private IntPtr _hwnd;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;

    public TerminalWindow(TopBarWindow topBar)
    {
        _settings = AppSettings.Current;
        _topBar = topBar;
        InitializeComponent();
        Title = "Veil Terminal";

        Activated += OnFirstActivated;
        Closed += OnClosed;
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        try
        {
            _hwnd = WindowHelper.GetHwnd(this);
            ConfigureWindowChrome();
            SetupAcrylic();
            ApplyWindowSize();
            LoadTerminalLogo();
            EnsureDefaultTab();
        }
        catch (Exception ex)
        {
            AppLogger.Error("TerminalWindow first activation failed.", ex);
        }
    }

    private void ConfigureWindowChrome()
    {
        WindowHelper.ApplyAppIcon(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var appWindow = WindowHelper.GetAppWindow(this);
        if (AppWindowTitleBar.IsCustomizationSupported() && appWindow is { TitleBar: { } titleBar })
        {
            titleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = global::Windows.UI.Color.FromArgb(30, 255, 255, 255);
            titleBar.ButtonPressedBackgroundColor = global::Windows.UI.Color.FromArgb(18, 255, 255, 255);
            titleBar.ButtonForegroundColor = global::Windows.UI.Color.FromArgb(200, 255, 255, 255);
            titleBar.ButtonInactiveForegroundColor = global::Windows.UI.Color.FromArgb(100, 255, 255, 255);
            titleBar.ButtonHoverForegroundColor = Colors.White;
        }

        int rounded = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref rounded, sizeof(int));
    }

    private void SetupAcrylic()
    {
        _acrylicController = new DesktopAcrylicController
        {
            TintColor = global::Windows.UI.Color.FromArgb(255, 10, 13, 20),
            TintOpacity = 0.88f,
            LuminosityOpacity = 0.25f,
            FallbackColor = global::Windows.UI.Color.FromArgb(240, 10, 13, 20)
        };

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark
        };

        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
    }

    private void ApplyWindowSize()
    {
        var monitors = MonitorService.GetAllMonitors();
        var primary = monitors.FirstOrDefault(static m => m.IsPrimary) ?? monitors.FirstOrDefault();
        if (primary is null)
        {
            return;
        }

        int screenWidth = primary.Bounds.Right - primary.Bounds.Left;
        int screenHeight = primary.Bounds.Bottom - primary.Bounds.Top;
        int w = Math.Max(800, (int)(screenWidth * 0.65));
        int h = Math.Max(520, (int)(screenHeight * 0.65));

        WindowHelper.PositionOnMonitor(
            this,
            primary.Bounds.Left + (screenWidth - w) / 2,
            primary.Bounds.Top + (screenHeight - h) / 2,
            w,
            h);
    }

    private void LoadTerminalLogo()
    {
        string logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal", "TerminalLogo.png");
        if (!File.Exists(logoPath))
        {
            return;
        }

        var bitmap = new BitmapImage(new Uri(logoPath));
        EmptyStateLogoImage.Source = bitmap;
    }

    private void OpenTab(TerminalProfile profile)
    {
        int tabId = Interlocked.Increment(ref s_nextTabId);
        var view = CreateTerminalView();
        var tab = new TerminalTab(tabId, profile, view);
        _tabs[tabId] = tab;

        AddTabButton(tab);
        TerminalHost.Children.Add(view);
        _topBar.OnTerminalTabAdded(tabId, tab.Title);
        ActivateTab(tabId);
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        StartTabSession(tab);
    }

    private NativeTerminalControl CreateTerminalView()
    {
        return new NativeTerminalControl(_settings.TerminalFontFamily, _settings.TerminalFontSize, _settings.TerminalScrollback)
        {
            Visibility = Visibility.Collapsed
        };
    }

    private void AddTabButton(TerminalTab tab)
    {
        var titleText = new TextBlock
        {
            Text = Truncate(tab.Title),
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(200, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 140,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var closeBtn = new Button
        {
            Content = "x",
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 255, 255, 255)),
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(150, 255, 255, 255)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            IsTabStop = false,
            Tag = tab.TabId
        };
        closeBtn.Click += OnCloseTabClick;
        closeBtn.PointerEntered += (_, _) => closeBtn.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(40, 255, 255, 255));
        closeBtn.PointerExited += (_, _) => closeBtn.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 255, 255, 255));

        var tabBtn = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 2,
                Children = { titleText, closeBtn }
            },
            Height = 28,
            Padding = new Thickness(10, 0, 6, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            IsTabStop = false,
            Tag = tab.TabId
        };
        tabBtn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(22, 255, 255, 255));
        tabBtn.Click += OnTabButtonClick;
        tab.TabButton = tabBtn;
        TabStrip.Children.Add(tabBtn);
    }

    private void StartTabSession(TerminalTab tab)
    {
        tab.View.ShowStatus($"Launching {tab.Profile.DisplayName}...", false);

        TerminalSession? session = null;
        try
        {
            int cols = Math.Max(_settings.TerminalCols, tab.View.CurrentCols);
            int rows = Math.Max(_settings.TerminalRows, tab.View.CurrentRows);
            session = new TerminalSession(tab.Profile, cols, rows);

            // Wire OutputReceived before the IsAlive check so the initial shell prompt
            // that arrives right after process launch is not discarded. TerminalSession
            // buffers output until the first handler is attached, then replays it.
            session.OutputReceived += tab.View.AppendOutput;
            session.SessionExited += () => DispatcherQueue.TryEnqueue(() => OnSessionExited(tab.TabId));

            if (!session.IsAlive)
            {
                throw new InvalidOperationException($"{tab.Profile.DisplayName} exited immediately after launch.");
            }

            tab.Session = session;
            tab.View.InputSubmitted += session.Write;
            tab.View.ResizeRequested += session.Resize;
            tab.View.TitleChanged += title => DispatcherQueue.TryEnqueue(() => UpdateTabTitle(tab.TabId, title));
            session.Resize(tab.View.CurrentCols, tab.View.CurrentRows);
            tab.View.HideStatus();
            tab.View.FocusTerminal();
        }
        catch (Exception ex)
        {
            session?.Dispose();
            AppLogger.Error($"Failed to start terminal session for {tab.Profile.DisplayName}.", ex);
            tab.View.ShowStatus(BuildLaunchFailureMessage(tab.Profile.DisplayName, ex), true);
            UpdateTabTitle(tab.TabId, $"{tab.Profile.DisplayName} (failed)");
        }
    }

    private static string BuildLaunchFailureMessage(string profileName, Exception ex)
    {
        string message = ex.Message?.Trim() ?? "Unknown startup error.";
        return $"Unable to start {profileName}. {message}";
    }

    private void ActivateTab(int tabId)
    {
        _activeTabId = tabId;

        foreach (var (id, tab) in _tabs)
        {
            bool active = id == tabId;
            tab.View.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            tab.TabButton.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(active ? (byte)36 : (byte)0, 255, 255, 255));
            if (active)
            {
                tab.View.FocusTerminal();
            }
        }

        _topBar.OnTerminalTabActivated(tabId);
        if (_tabs.TryGetValue(tabId, out var activeTab))
        {
            Title = $"{activeTab.Title} - Veil Terminal";
        }
        else
        {
            Title = "Veil Terminal";
        }
    }

    private void UpdateTabTitle(int tabId, string title)
    {
        if (!_tabs.TryGetValue(tabId, out var tab))
        {
            return;
        }

        tab.Title = title;
        if (tab.TabButton.Content is StackPanel sp && sp.Children[0] is TextBlock tb)
        {
            tb.Text = Truncate(title);
        }

        _topBar.OnTerminalTabTitleChanged(tabId, title);

        if (tabId == _activeTabId)
        {
            Title = $"{title} - Veil Terminal";
        }
    }

    private void CloseTab(int tabId)
    {
        if (!_tabs.TryGetValue(tabId, out var tab) || tab.IsClosed)
        {
            return;
        }

        tab.IsClosed = true;

        try
        {
            tab.Session?.Dispose();
        }
        catch
        {
        }

        TerminalHost.Children.Remove(tab.View);
        TabStrip.Children.Remove(tab.TabButton);
        _tabs.Remove(tabId);
        _topBar.OnTerminalTabClosed(tabId);

        if (_activeTabId == tabId)
        {
            _activeTabId = -1;
            int remaining = _tabs.Keys.LastOrDefault(-1);
            if (remaining >= 0)
            {
                ActivateTab(remaining);
            }
            else
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
                Title = "Veil Terminal";
            }
        }
    }

    private void OnSessionExited(int tabId)
    {
        if (!_tabs.TryGetValue(tabId, out var tab) || tab.IsClosed)
        {
            return;
        }

        uint? exitCode = tab.Session?.ExitCode;
        if (exitCode is { } code && code != 0)
        {
            try
            {
                tab.Session?.Dispose();
            }
            catch
            {
            }

            tab.Session = null;
            tab.View.ShowStatus(BuildSessionExitMessage(tab.Profile.DisplayName, code), true);
            UpdateTabTitle(tab.TabId, $"{tab.Profile.DisplayName} (exited)");
            return;
        }

        CloseTab(tabId);
    }

    private static string BuildSessionExitMessage(string profileName, uint exitCode)
    {
        return $"{profileName} exited with code {TerminalSession.FormatExitCode(exitCode)}.";
    }

    private void ShowProfilePicker()
    {
        var profiles = ShellProfileService.GetProfiles();
        ProfileList.Children.Clear();

        foreach (var profile in profiles)
        {
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Height = 36,
                Padding = new Thickness(16, 0, 16, 0),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 255, 255, 255)),
                CornerRadius = new CornerRadius(0),
                IsTabStop = false,
                Tag = profile
            };

            btn.Content = new TextBlock
            {
                Text = profile.DisplayName,
                FontSize = 13,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(220, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };

            btn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(22, 255, 255, 255));
            btn.Click += OnProfilePickerItemClick;
            ProfileList.Children.Add(btn);
        }

        ProfilePickerOverlay.Visibility = Visibility.Visible;
        ProfilePickerOverlay.PointerPressed += OnProfilePickerBackgroundClick;
    }

    private void HideProfilePicker()
    {
        ProfilePickerOverlay.Visibility = Visibility.Collapsed;
        ProfilePickerOverlay.PointerPressed -= OnProfilePickerBackgroundClick;
    }

    internal void OnNewTabClick(object? sender, RoutedEventArgs? e)
    {
        var profiles = ShellProfileService.GetProfiles();

        if (profiles.Count == 1)
        {
            HideProfilePicker();
            OpenTab(profiles[0]);
        }
        else
        {
            ShowProfilePicker();
        }
    }

    private void OnProfilePickerItemClick(object sender, RoutedEventArgs e)
    {
        HideProfilePicker();
        if (sender is Button { Tag: TerminalProfile profile })
        {
            OpenTab(profile);
        }
    }

    private void OnProfilePickerBackgroundClick(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        HideProfilePicker();
    }

    internal void ExternalActivateTab(int tabId)
    {
        ActivateTab(tabId);
    }

    internal void ExternalCloseTab(int tabId)
    {
        CloseTab(tabId);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        foreach (TerminalTab tab in _tabs.Values.ToArray())
        {
            tab.IsClosed = true;
            try
            {
                tab.Session?.Dispose();
            }
            catch
            {
            }
        }

        _tabs.Clear();
        _topBar.OnTerminalClosed();
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfig = null;
    }

    internal void EnsureDefaultTab()
    {
        if (_tabs.Count > 0)
        {
            return;
        }

        var profile = ShellProfileService.GetById(_settings.TerminalDefaultProfileId)
                      ?? ShellProfileService.GetDefaultProfile();
        OpenTab(profile);
    }

    internal void OpenDefaultTab() => EnsureDefaultTab();

    private void OnTabButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int id })
        {
            ActivateTab(id);
        }
    }

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int id })
        {
            CloseTab(id);
        }
    }

    private static string Truncate(string s) =>
        string.IsNullOrWhiteSpace(s) ? "Terminal" : s.Length > 28 ? s[..28] + "..." : s;
}
