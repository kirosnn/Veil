using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Veil.Diagnostics;
using Veil.Interop;
using Veil.Services.Terminal;
using WinRT;
using static Veil.Interop.NativeMethods;

namespace VeilTerminal;

public sealed partial class TerminalMainWindow : Window
{
    private static int s_nextTabId;

    private sealed class TerminalTab
    {
        public int TabId { get; }
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

    private readonly TerminalSettings _settings;
    private readonly Dictionary<int, TerminalTab> _tabs = [];
    private int _activeTabId = -1;
    private IntPtr _hwnd;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;

    public TerminalMainWindow()
    {
        _settings = TerminalSettings.Current;
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
            LoadLogo();
            EnsureDefaultTab();
        }
        catch (Exception ex)
        {
            AppLogger.Error("TerminalMainWindow activation failed.", ex);
        }
    }

    private void ConfigureWindowChrome()
    {
        WindowHelper.ApplyAppIcon(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var appWindow = WindowHelper.GetAppWindow(this);
        if (AppWindowTitleBar.IsCustomizationSupported() && appWindow?.TitleBar is { } titleBar)
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
            TintOpacity = 0.85f,
            LuminosityOpacity = 0.3f,
            FallbackColor = global::Windows.UI.Color.FromArgb(230, 10, 13, 20)
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

        int sw = primary.Bounds.Right - primary.Bounds.Left;
        int sh = primary.Bounds.Bottom - primary.Bounds.Top;
        int w = Math.Max(900, (int)(sw * 0.65));
        int h = Math.Max(560, (int)(sh * 0.65));

        WindowHelper.PositionOnMonitor(
            this,
            primary.Bounds.Left + (sw - w) / 2,
            primary.Bounds.Top + (sh - h) / 2,
            w,
            h);
    }

    private void LoadLogo()
    {
        string logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal", "TerminalLogo.png");
        if (!File.Exists(logoPath))
        {
            return;
        }

        var bitmap = new BitmapImage(new Uri(logoPath));
        EmptyStateLogoImage.Source = bitmap;
    }

    internal void OpenTab(TerminalProfile profile)
    {
        int tabId = Interlocked.Increment(ref s_nextTabId);
        var view = CreateTerminalView();
        var tab = new TerminalTab(tabId, profile, view);
        _tabs[tabId] = tab;

        AddTabButton(tab);
        TerminalHost.Children.Add(view);
        ActivateTab(tabId);
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        StartTabSession(tab);
    }

    private NativeTerminalControl CreateTerminalView()
    {
        return new NativeTerminalControl(_settings.FontFamily, _settings.FontSize, _settings.Scrollback)
        {
            Visibility = Visibility.Collapsed
        };
    }

    private void StartTabSession(TerminalTab tab)
    {
        tab.View.ShowStatus($"Launching {tab.Profile.DisplayName}...", false);

        TerminalSession? session = null;
        try
        {
            int cols = Math.Max(_settings.Cols, tab.View.CurrentCols);
            int rows = Math.Max(_settings.Rows, tab.View.CurrentRows);
            session = new TerminalSession(tab.Profile, cols, rows);

            // Wire OutputReceived before the IsAlive check so the initial shell prompt
            // that arrives right after process launch is not discarded. TerminalSession
            // buffers output until the first handler is attached, then replays it.
            session.OutputReceived += tab.View.AppendOutput;
            session.SessionExited += () => DispatcherQueue.TryEnqueue(() => HandleSessionExited(tab.TabId));

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

        if (tabId == _activeTabId)
        {
            Title = $"{title} - Veil Terminal";
        }
    }

    private void HandleSessionExited(int tabId)
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

        if (_activeTabId == tabId)
        {
            _activeTabId = -1;
            int next = _tabs.Keys.LastOrDefault(-1);
            if (next >= 0)
            {
                ActivateTab(next);
            }
            else
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
                Title = "Veil Terminal";
            }
        }
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
                Tag = profile,
                Content = new TextBlock
                {
                    Text = profile.DisplayName,
                    FontSize = 13,
                    FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
                    Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(220, 255, 255, 255)),
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            btn.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(22, 255, 255, 255));
            btn.Click += OnProfilePickerItemClick;
            ProfileList.Children.Add(btn);
        }

        ProfilePickerOverlay.Visibility = Visibility.Visible;
        ProfilePickerOverlay.PointerPressed += OnPickerBackgroundClick;
    }

    private void HideProfilePicker()
    {
        ProfilePickerOverlay.Visibility = Visibility.Collapsed;
        ProfilePickerOverlay.PointerPressed -= OnPickerBackgroundClick;
    }

    private void OnNewTabClick(object sender, RoutedEventArgs e)
    {
        var profiles = ShellProfileService.GetProfiles();
        if (profiles.Count == 1)
        {
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

    private void OnPickerBackgroundClick(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        HideProfilePicker();
    }

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

        var profile = ShellProfileService.GetById(_settings.DefaultProfileId)
                      ?? ShellProfileService.GetDefaultProfile();
        OpenTab(profile);
    }

    internal void OpenDefaultTab() => EnsureDefaultTab();

    private static string Truncate(string s) =>
        string.IsNullOrWhiteSpace(s) ? "Terminal" : s.Length > 28 ? s[..28] + "..." : s;
}
