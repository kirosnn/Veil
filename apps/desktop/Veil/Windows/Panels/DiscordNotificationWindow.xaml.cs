using Veil.Interop;
using Veil.Configuration;
using Veil.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class DiscordNotificationWindow : Window
{
    private const int PanelWidth = 320;
    private const int MinimumPanelWidth = 280;
    private const int PanelCornerRadius = 14;
    private const int ContentInset = 16;
    private const int MinimumPanelHeight = 200;
    private const int PanelWidthPadding = 16;
    private const int PanelHeightPadding = 22;
    private static readonly ImageSource DiscordLightIconSource = new SvgImageSource(new Uri("ms-appx:///Assets/Icons/discord.svg"));
    private static readonly ImageSource DiscordDarkIconSource = new SvgImageSource(new Uri("ms-appx:///Assets/Icons/discord-dark.svg"));
    private static readonly FontFamily IconFont = new("Segoe MDL2 Assets");
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    private DiscordNotificationService _discordService = null!;
    private readonly AppSettings _settings = AppSettings.Current;
    private IntPtr _hwnd;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private readonly DispatcherTimer _visibilityTimer;
    private DateTime _openedAtUtc;
    private int _panelHeight = 300;
    private int _panelWidth = PanelWidth;

    public bool IsMenuVisible { get; private set; }
    public DateTime LastHiddenAtUtc { get; private set; }

    public DiscordNotificationWindow()
    {
        InitializeComponent();
        Title = "Discord Notifications";

        _visibilityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _visibilityTimer.Tick += OnVisibilityTick;

        Activated += OnFirstActivated;
        Activated += OnWindowActivated;
        Closed += OnClosed;
        _settings.Changed += OnSettingsChanged;
    }

    private bool UseLightTheme => false;

    internal void SetDiscordService(DiscordNotificationService service)
    {
        if (_discordService == service)
        {
            return;
        }

        if (_discordService is not null)
        {
            _discordService.NotificationsChanged -= OnNotificationsChanged;
        }

        _discordService = service;
        _discordService.NotificationsChanged += OnNotificationsChanged;
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        _hwnd = WindowHelper.GetHwnd(this);
        WindowHelper.RemoveTitleBar(this);

        int exStyle = GetWindowLongW(_hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        exStyle &= ~WS_EX_NOACTIVATE;
        SetWindowLongW(_hwnd, GWL_EXSTYLE, exStyle);

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        WindowHelper.PrepareForSystemBackdrop(this);
        SetupAcrylic();
        BuildUI(0, 0);
        ShowWindowNative(_hwnd, SW_HIDE);
    }

    private void SetupAcrylic()
    {
        _acrylicController?.Dispose();

        _acrylicController = new DesktopAcrylicController
        {
            TintColor = PanelGlassPalette.GetAcrylicTintColor(UseLightTheme),
            TintOpacity = PanelGlassPalette.GetAcrylicTintOpacity(UseLightTheme),
            LuminosityOpacity = PanelGlassPalette.GetAcrylicLuminosityOpacity(UseLightTheme),
            FallbackColor = PanelGlassPalette.GetAcrylicFallbackColor(UseLightTheme)
        };

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = PanelGlassPalette.GetBackdropTheme(UseLightTheme)
        };

        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
        PanelBorder.Background = PanelGlassPalette.CreateFrameBrush(UseLightTheme, lightAlpha: 24, darkAlpha: 10);
    }

    private PanelWindowMetrics BuildUI(int anchorRight, int anchorY)
    {
        ContentPanel.Children.Clear();

        var shell = new Border
        {
            CornerRadius = new CornerRadius(PanelCornerRadius),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var root = new StackPanel
        {
            Margin = new Thickness(ContentInset),
            Spacing = 12
        };

        root.Children.Add(BuildHeader());
        root.Children.Add(BuildSummaryPanel());
        root.Children.Add(BuildActionsRow());

        DiscordCallSnapshot snapshot = _discordService.CallSnapshot;
        if (snapshot.HasVoiceConnection || snapshot.HasIncomingCall)
        {
            root.Children.Add(BuildCallControlCard(snapshot));
        }

        root.Children.Add(BuildNotificationList());

        shell.Child = root;
        PanelWindowMetrics metrics = PanelWindowSizer.Measure(
            this,
            shell,
            anchorRight,
            anchorY,
            PanelWidth,
            MinimumPanelWidth,
            MinimumPanelHeight,
            PanelWidthPadding,
            PanelHeightPadding);
        if (metrics.IsHeightClamped)
        {
            shell.Child = CreateScrollableContent(root);
            shell.Width = metrics.ViewWidth;
            shell.MaxWidth = metrics.ViewWidth;
            shell.Height = metrics.ViewHeight;
        }
        _panelWidth = metrics.Width;
        _panelHeight = metrics.Height;
        shell.Height = metrics.ViewHeight;
        ContentPanel.Children.Add(shell);
        return metrics;
    }

    private static ScrollViewer CreateScrollableContent(UIElement content)
    {
        return new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(0, 0, 6, 0),
                Child = content
            },
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private UIElement BuildHeader()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Image
        {
            Source = UseLightTheme ? DiscordDarkIconSource : DiscordLightIconSource,
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(icon, 0);

        var title = new TextBlock
        {
            Text = "Discord",
            FontSize = 22,
            FontFamily = (FontFamily)Application.Current.Resources["SfDisplaySemibold"],
            Foreground = CreatePrimaryBrush(),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 1);

        grid.Children.Add(icon);
        grid.Children.Add(title);
        return grid;
    }

    private UIElement BuildSummaryPanel()
    {
        DiscordCallSnapshot snapshot = _discordService.CallSnapshot;
        bool hasNotifications = _discordService.UnreadCount > 0;

        global::Windows.UI.Color accentColor = snapshot.HasIncomingCall || snapshot.HasVoiceConnection
            ? global::Windows.UI.Color.FromArgb(255, 87, 242, 135)
            : hasNotifications
                ? global::Windows.UI.Color.FromArgb(255, 88, 101, 242)
                : UseLightTheme
                    ? global::Windows.UI.Color.FromArgb(255, 32, 32, 32)
                    : global::Windows.UI.Color.FromArgb(255, 235, 235, 235);

        string headline = snapshot.HasIncomingCall
            ? "Incoming call"
            : snapshot.HasVoiceConnection
                ? "Voice connected"
                : hasNotifications
                    ? $"{_discordService.UnreadCount} unread"
                    : _discordService.IsDiscordRunning
                        ? "Discord is running"
                        : "Discord is quiet";

        string body = snapshot.HasIncomingCall || snapshot.HasVoiceConnection
            ? GetCallDetail(snapshot)
            : hasNotifications
                ? "Triage the latest activity here before opening the full client."
                : _discordService.IsDiscordRunning
                    ? "Keep Discord in the background and only bring it back when you need it."
                    : "No recent Discord activity is waiting right now.";

        var border = new Border
        {
            Background = UseLightTheme
                ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(16, 0, 0, 0))
                : new SolidColorBrush(global::Windows.UI.Color.FromArgb(16, 255, 255, 255)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12)
        };

        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(new TextBlock
        {
            Text = headline,
            FontSize = 14,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
            Foreground = new SolidColorBrush(accentColor)
        });
        panel.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = CreateSecondaryBrush(),
            TextWrapping = TextWrapping.WrapWholeWords
        });

        border.Child = panel;
        return border;
    }

    private UIElement BuildActionsRow()
    {
        var grid = new Grid
        {
            ColumnSpacing = 8
        };

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var openButton = BuildWideActionButton("Open", true, OnOpenDiscordClick);
        Grid.SetColumn(openButton, 0);

        var hideButton = BuildWideActionButton("Hide", _discordService.IsDiscordRunning, OnHideDiscordClick);
        Grid.SetColumn(hideButton, 1);

        var clearButton = BuildWideActionButton("Clear", _discordService.Notifications.Count > 0, OnClearDiscordNotificationsClick);
        Grid.SetColumn(clearButton, 2);

        grid.Children.Add(openButton);
        grid.Children.Add(hideButton);
        grid.Children.Add(clearButton);
        return grid;
    }

    private Button BuildWideActionButton(string label, bool isEnabled, RoutedEventHandler onClick)
    {
        var button = PanelButtonFactory.Create(
            label,
            UseLightTheme
                ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(20, 0, 0, 0))
                : new SolidColorBrush(global::Windows.UI.Color.FromArgb(20, 255, 255, 255)),
            CreatePrimaryBrush(),
            UseLightTheme
                ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(30, 0, 0, 0))
                : new SolidColorBrush(global::Windows.UI.Color.FromArgb(30, 255, 255, 255)),
            UseLightTheme
                ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(38, 0, 0, 0))
                : new SolidColorBrush(global::Windows.UI.Color.FromArgb(38, 255, 255, 255)),
            onClick,
            height: 34,
            cornerRadius: new CornerRadius(10),
            isEnabled: isEnabled);
        button.FontSize = 12;
        button.FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"];
        return button;
    }

    private UIElement BuildCallControlCard(DiscordCallSnapshot snapshot)
    {
        var border = new Border
        {
            Background = UseLightTheme
                ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(18, 0, 0, 0))
                : new SolidColorBrush(global::Windows.UI.Color.FromArgb(22, 255, 255, 255)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 12, 14, 12)
        };

        var root = new StackPanel { Spacing = 12 };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var stateIcon = new TextBlock
        {
            Text = "\uE768",
            FontFamily = IconFont,
            FontSize = 16,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 87, 242, 135)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 10, 0)
        };
        Grid.SetColumn(stateIcon, 0);

        var textPanel = new StackPanel { Spacing = 1 };
        textPanel.Children.Add(new TextBlock
        {
            Text = snapshot.HasIncomingCall ? "Incoming call" : "Voice connected",
            FontSize = 16,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 87, 242, 135))
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = GetCallDetail(snapshot),
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = CreateSecondaryBrush(),
            TextWrapping = TextWrapping.WrapWholeWords,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(textPanel, 1);

        var headerAction = BuildHeaderCallActionButton(
            snapshot.HasIncomingCall ? "\uE768" : "\uE717",
            snapshot.HasIncomingCall ? "Answer" : "Disconnect",
            snapshot.HasIncomingCall ? snapshot.CanAnswer : snapshot.CanDisconnect,
            snapshot.HasIncomingCall ? DiscordCallAction.Answer : DiscordCallAction.Disconnect);
        Grid.SetColumn(headerAction, 2);

        headerGrid.Children.Add(stateIcon);
        headerGrid.Children.Add(textPanel);
        headerGrid.Children.Add(headerAction);
        root.Children.Add(headerGrid);

        var controlsGrid = new Grid
        {
            ColumnSpacing = 8
        };

        for (int index = 0; index < 4; index++)
        {
            controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var muteButton = BuildCallTile("\uE720", "Mute", snapshot.CanToggleMute, DiscordCallAction.ToggleMute);
        Grid.SetColumn(muteButton, 0);
        controlsGrid.Children.Add(muteButton);

        var deafenButton = BuildCallTile("\uE74F", "Deafen", snapshot.CanToggleDeafen, DiscordCallAction.ToggleDeafen);
        Grid.SetColumn(deafenButton, 1);
        controlsGrid.Children.Add(deafenButton);

        var cameraButton = BuildCallTile("\uE714", "Video", snapshot.CanToggleCamera, DiscordCallAction.ToggleCamera);
        Grid.SetColumn(cameraButton, 2);
        controlsGrid.Children.Add(cameraButton);

        var shareButton = BuildCallTile("\uE7F4", "Share", snapshot.CanToggleScreenShare, DiscordCallAction.ToggleScreenShare);
        Grid.SetColumn(shareButton, 3);
        controlsGrid.Children.Add(shareButton);

        root.Children.Add(controlsGrid);
        border.Child = root;
        return border;
    }

    private Button BuildHeaderCallActionButton(string glyph, string label, bool isEnabled, DiscordCallAction action)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = 15,
            Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var button = PanelButtonFactory.Create(
            icon,
            new SolidColorBrush(global::Windows.UI.Color.FromArgb(34, 87, 242, 135)),
            new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255)),
            new SolidColorBrush(global::Windows.UI.Color.FromArgb(54, 87, 242, 135)),
            new SolidColorBrush(global::Windows.UI.Color.FromArgb(70, 87, 242, 135)),
            OnDiscordCallActionClick,
            width: 40,
            height: 40,
            cornerRadius: new CornerRadius(12),
            tag: action,
            isEnabled: isEnabled,
            horizontalAlignment: HorizontalAlignment.Right,
            verticalAlignment: VerticalAlignment.Center);

        ToolTipService.SetToolTip(button, label);
        return button;
    }

    private Button BuildCallTile(string glyph, string label, bool isEnabled, DiscordCallAction action)
    {
        var stack = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = 16,
            Foreground = CreatePrimaryBrush(),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
            Foreground = CreateSecondaryBrush(),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        var button = PanelButtonFactory.Create(
            stack,
            UseLightTheme
                ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(22, 0, 0, 0))
                : new SolidColorBrush(global::Windows.UI.Color.FromArgb(22, 255, 255, 255)),
            CreatePrimaryBrush(),
            UseLightTheme
                ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(32, 0, 0, 0))
                : new SolidColorBrush(global::Windows.UI.Color.FromArgb(32, 255, 255, 255)),
            UseLightTheme
                ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(42, 0, 0, 0))
                : new SolidColorBrush(global::Windows.UI.Color.FromArgb(42, 255, 255, 255)),
            OnDiscordCallActionClick,
            height: 62,
            cornerRadius: new CornerRadius(12),
            tag: action,
            isEnabled: isEnabled,
            horizontalAlignment: HorizontalAlignment.Stretch);
        return button;
    }

    private UIElement BuildNotificationList()
    {
        var notifications = _discordService?.Notifications ?? [];
        bool isDiscordRunning = _discordService?.IsDiscordRunning ?? false;

        if (notifications.Count == 0)
        {
            var emptyPanel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 8,
                Margin = new Thickness(0, 24, 0, 24)
            };

            var emptyIcon = new TextBlock
            {
                Text = "\uE7BA",
                FontFamily = IconFont,
                FontSize = 28,
                Foreground = CreateSecondaryBrush(),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var emptyText = new TextBlock
            {
                Text = isDiscordRunning ? "Discord is running quietly" : "No notifications",
                FontSize = 14,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
                Foreground = CreateSecondaryBrush(),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            emptyPanel.Children.Add(emptyIcon);
            emptyPanel.Children.Add(emptyText);
            return emptyPanel;
        }

        var list = new StackPanel { Spacing = 6 };

        int shown = 0;
        for (int i = notifications.Count - 1; i >= 0 && shown < 8; i--, shown++)
        {
            list.Children.Add(BuildNotificationCard(notifications[i]));
        }

        if (notifications.Count > 8)
        {
            list.Children.Add(new TextBlock
            {
                Text = $"+{notifications.Count - 8} more",
                FontSize = 12,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
                Foreground = CreateSecondaryBrush(),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        return list;
    }

    private UIElement BuildNotificationCard(DiscordNotification notif)
    {
        var border = new Border
        {
            Background = UseLightTheme
                ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(18, 0, 0, 0))
                : new SolidColorBrush(global::Windows.UI.Color.FromArgb(18, 255, 255, 255)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10)
        };

        var panel = new StackPanel { Spacing = 3 };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleText = new TextBlock
        {
            Text = notif.Title,
            FontSize = 13,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
            Foreground = CreatePrimaryBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        Grid.SetColumn(titleText, 0);

        var timeText = new TextBlock
        {
            Text = FormatTime(notif.Time),
            FontSize = 11,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = CreateSecondaryBrush(),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(timeText, 1);

        headerGrid.Children.Add(titleText);
        headerGrid.Children.Add(timeText);
        panel.Children.Add(headerGrid);

        if (!string.IsNullOrWhiteSpace(notif.Body))
        {
            panel.Children.Add(new TextBlock
            {
                Text = notif.Body,
                FontSize = 12,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
                Foreground = CreateSecondaryBrush(),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.WrapWholeWords,
                MaxLines = 2
            });
        }

        border.Child = panel;
        return border;
    }

    private string GetCallDetail(DiscordCallSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.StatusDetail))
        {
            return snapshot.StatusDetail;
        }

        DiscordNotification? latestCall = GetLatestCallNotification();
        if (latestCall is not null)
        {
            if (!string.IsNullOrWhiteSpace(latestCall.Body))
            {
                return latestCall.Body;
            }

            if (!string.IsNullOrWhiteSpace(latestCall.Title))
            {
                return latestCall.Title;
            }
        }

        return snapshot.HasIncomingCall
            ? "Open Discord to answer quickly."
            : "Open Discord or discord.com to manage the current voice session.";
    }

    private DiscordNotification? GetLatestCallNotification()
    {
        DiscordNotification? latest = null;
        foreach (DiscordNotification notification in _discordService.Notifications)
        {
            string combined = $"{notification.Title} {notification.Body}";
            bool isCallNotification = combined.Contains("incoming call", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("appel entrant", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("is calling", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("vous appelle", StringComparison.OrdinalIgnoreCase);
            if (!isCallNotification)
            {
                continue;
            }

            if (latest is null || notification.Time > latest.Time)
            {
                latest = notification;
            }
        }

        return latest;
    }

    private static string FormatTime(DateTime time)
    {
        TimeSpan diff = DateTime.Now - time;
        if (diff.TotalMinutes < 1) return "now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h";
        return time.ToString("MMM d");
    }

    private SolidColorBrush CreatePrimaryBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(240, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(248, 255, 255, 255));
    }

    private SolidColorBrush CreateSecondaryBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(160, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(214, 255, 255, 255));
    }

    private void OnOpenDiscordClick(object sender, RoutedEventArgs e)
    {
        DiscordAppController.LaunchOrActivate();
        Hide();
    }

    private void OnHideDiscordClick(object sender, RoutedEventArgs e)
    {
        DiscordAppController.MinimizeAllWindows();
        Hide();
    }

    private async void OnClearDiscordNotificationsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            button.IsEnabled = false;
        }

        try
        {
            await _discordService.ClearNotificationsAsync();
            RefreshLayout();
        }
        finally
        {
            if (sender is Button buttonAfter)
            {
                buttonAfter.IsEnabled = true;
            }
        }
    }

    private async void OnDiscordCallActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DiscordCallAction action } button)
        {
            return;
        }

        button.IsEnabled = false;

        try
        {
            if (action == DiscordCallAction.Answer)
            {
                DiscordAppController.LaunchOrActivate();
            }

            DiscordAppController.TryInvokeCallAction(action);
            await Task.Delay(180);
            RefreshLayout();
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    public void Initialize()
    {
        var appWindow = WindowHelper.GetAppWindow(this);
        appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(-9999, -9999, 1, 1));
        Activate();
    }

    public void ShowAt(int x, int y)
    {
        PanelWindowMetrics metrics = BuildUI(x, y + 6);

        var appWindow = WindowHelper.GetAppWindow(this);
        appWindow.MoveAndResize(metrics.Bounds);
        WindowHelper.ApplyRoundedRegion(_hwnd, metrics.Bounds.Width, metrics.Bounds.Height, PanelCornerRadius);

        appWindow.Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        Activate();
        SetForegroundWindow(_hwnd);

        IsMenuVisible = true;
        _openedAtUtc = DateTime.UtcNow;
        _visibilityTimer.Start();
    }

    public void Hide()
    {
        _visibilityTimer.Stop();
        ShowWindowNative(_hwnd, SW_HIDE);
        IsMenuVisible = false;
        LastHiddenAtUtc = DateTime.UtcNow;
    }

    public void RefreshLayout()
    {
        if (_hwnd == IntPtr.Zero) return;

        SetupAcrylic();

        if (!IsMenuVisible)
        {
            BuildUI(0, 0);
            return;
        }

        if (!GetWindowRect(_hwnd, out var rect)) return;

        PanelWindowMetrics metrics = BuildUI(rect.Right, rect.Top);

        var appWindow = WindowHelper.GetAppWindow(this);
        appWindow.MoveAndResize(metrics.Bounds);
        WindowHelper.ApplyRoundedRegion(_hwnd, metrics.Bounds.Width, metrics.Bounds.Height, PanelCornerRadius);
    }

    private void OnSettingsChanged()
    {
        DispatcherQueue.TryEnqueue(RefreshLayout);
    }

    private void OnNotificationsChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsMenuVisible)
            {
                RefreshLayout();
            }
        });
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _settings.Changed -= OnSettingsChanged;
        if (_discordService is not null)
        {
            _discordService.NotificationsChanged -= OnNotificationsChanged;
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!IsMenuVisible) return;
        if (args.WindowActivationState != WindowActivationState.Deactivated) return;
        if ((DateTime.UtcNow - _openedAtUtc).TotalMilliseconds < 180) return;
        Hide();
    }

    private void OnVisibilityTick(object? sender, object e)
    {
        if (!IsMenuVisible) return;
        if ((DateTime.UtcNow - _openedAtUtc).TotalMilliseconds < 220) return;
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd) return;
        Hide();
    }

}
