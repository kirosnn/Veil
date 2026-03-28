using Veil.Interop;
using Veil.Configuration;
using Veil.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Media;
using WinRT;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class MusicControlWindow : Window
{
    private const int PanelWidth = 336;
    private const int MinimumPanelWidth = 300;
    private const int PanelCornerRadius = 14;
    private const int ContentInset = 16;
    private const int MinimumPanelHeight = 260;
    private const int PanelWidthPadding = 16;
    private const int PanelHeightPadding = 22;
    private const int CoverSize = 248;
    private const int CardCornerRadius = 24;
    private static readonly FontFamily IconFont = new("Segoe MDL2 Assets");
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    private MediaControlService _mediaService = null!;
    private readonly MediaSourceWindowService _sourceWindowService = new();
    private readonly AppSettings _settings = AppSettings.Current;
    private IntPtr _hwnd;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private readonly DispatcherTimer _visibilityTimer;
    private readonly DispatcherTimer _progressTimer;
    private DateTime _openedAtUtc;

    private TextBlock? _titleText;
    private TextBlock? _artistText;
    private TextBlock? _sourceWindowIcon;
    private TextBlock? _sourceWindowLabel;
    private ImageBrush? _coverArtBrush;
    private Button? _previousButton;
    private Button? _playPauseButton;
    private Button? _nextButton;
    private Button? _sourceWindowButton;

    private Grid? _progressTrack;
    private Border? _progressFill;
    private double _progressTrackWidth;
    private TextBlock? _positionText;
    private TextBlock? _remainingText;
    private bool _isSeeking;

    private Grid? _volumeTrack;
    private Border? _volumeFill;
    private double _volumeTrackWidth;
    private bool _isAdjustingVolume;
    private int _albumArtLoadVersion;
    private int _panelHeight = MinimumPanelHeight;
    private int _panelWidth = PanelWidth;

    public bool IsMenuVisible { get; private set; }
    public DateTime LastHiddenAtUtc { get; private set; }

    public MusicControlWindow()
    {
        InitializeComponent();
        Title = "Music Control";

        _visibilityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _visibilityTimer.Tick += OnVisibilityTick;

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _progressTimer.Tick += OnProgressTick;

        Activated += OnFirstActivated;
        Activated += OnWindowActivated;
        Closed += OnClosed;
        _settings.Changed += OnSettingsChanged;
    }

    private bool UseLightTheme => _settings.TopBarPanelTheme == "Light";

    internal void SetMediaService(MediaControlService service)
    {
        _mediaService = service;
        _mediaService.StateChanged += OnMediaStateChanged;
        _mediaService.TimelineChanged += OnTimelineChanged;
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

        SetupAcrylic();
        BuildUI(0, 0);
        ShowWindowNative(_hwnd, SW_HIDE);
    }

    private void SetupAcrylic()
    {
        _acrylicController?.Dispose();

        _acrylicController = new DesktopAcrylicController
        {
            TintColor = UseLightTheme
                ? global::Windows.UI.Color.FromArgb(255, 255, 255, 255)
                : global::Windows.UI.Color.FromArgb(255, 0, 0, 0),
            TintOpacity = UseLightTheme ? 0.06f : 0.58f,
            LuminosityOpacity = UseLightTheme ? 0.76f : 0.24f,
            FallbackColor = UseLightTheme
                ? global::Windows.UI.Color.FromArgb(214, 255, 255, 255)
                : global::Windows.UI.Color.FromArgb(228, 0, 0, 0)
        };

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = UseLightTheme ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark
        };

        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
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

        var root = new Grid
        {
            Margin = new Thickness(ContentInset)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _coverArtBrush = new ImageBrush
        {
            Stretch = Stretch.UniformToFill,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center
        };

        var artworkCard = new Border
        {
            Height = CoverSize,
            CornerRadius = new CornerRadius(CardCornerRadius),
            Background = _coverArtBrush
        };
        Grid.SetRow(artworkCard, 0);
        root.Children.Add(artworkCard);

        FrameworkElement topInfoPanel = BuildTrackInfoSection();
        topInfoPanel.Margin = new Thickness(4, 10, 4, 0);
        Grid.SetRow(topInfoPanel, 1);
        root.Children.Add(topInfoPanel);

        var layout = new StackPanel
        {
            Spacing = 9,
            Margin = new Thickness(0, 6, 0, 0)
        };
        layout.Children.Add(BuildProgressSection());
        layout.Children.Add(BuildTransportSection());
        if (_settings.MusicShowVolume)
        {
            layout.Children.Add(BuildVolumeSection());
        }

        if (_settings.MusicShowSourceToggle)
        {
            var sourceSection = BuildSourceWindowSection();
            sourceSection.Margin = new Thickness(0, 0, 0, 0);
            layout.Children.Add(sourceSection);
        }

        Grid.SetRow(layout, 2);
        root.Children.Add(layout);

        shell.Child = root;
        PanelWindowMetrics metrics = PanelWindowSizer.Measure(
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
            shell.Width = metrics.Width;
            shell.MaxWidth = metrics.Width;
            shell.Height = metrics.Height;
        }
        _panelWidth = metrics.Width;
        _panelHeight = metrics.Height;
        shell.Height = _panelHeight;
        ContentPanel.Children.Add(shell);

        _ = LoadAlbumArtAsync();
        UpdateUI();
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

    private void OnSettingsChanged()
    {
        DispatcherQueue.TryEnqueue(RefreshLayout);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _settings.Changed -= OnSettingsChanged;
        _mediaService.StateChanged -= OnMediaStateChanged;
        _mediaService.TimelineChanged -= OnTimelineChanged;
    }

    private FrameworkElement BuildTrackInfoSection()
    {
        _titleText = new TextBlock
        {
            Text = _mediaService.TrackTitle ?? "Not Playing",
            FontSize = 27,
            FontFamily = (FontFamily)Application.Current.Resources["SfDisplaySemibold"],
            Foreground = CreateTitleBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2
        };

        _artistText = new TextBlock
        {
            Text = _mediaService.TrackArtist ?? "",
            FontSize = 15,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = CreateArtistBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };

        var textPanel = new StackPanel
        {
            Spacing = 2
        };
        textPanel.Children.Add(_titleText);
        textPanel.Children.Add(_artistText);
        return textPanel;
    }

    private UIElement BuildProgressSection()
    {
        var section = new StackPanel
        {
            Spacing = 5
        };

        _progressTrack = new Grid
        {
            Height = 18,
            MinHeight = 18,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent)
        };

        var trackBg = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = CreateTrackBackgroundBrush(),
            VerticalAlignment = VerticalAlignment.Center
        };

        _progressFill = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = CreateTrackFillBrush(),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 0
        };

        _progressTrack.Children.Add(trackBg);
        _progressTrack.Children.Add(_progressFill);

        _progressTrack.SizeChanged += (_, e) => _progressTrackWidth = e.NewSize.Width;
        _progressTrack.PointerPressed += OnProgressPointerPressed;
        _progressTrack.PointerMoved += OnProgressPointerMoved;
        _progressTrack.PointerReleased += OnProgressPointerReleased;
        _progressTrack.PointerCaptureLost += OnProgressPointerCaptureLost;

        _positionText = new TextBlock
        {
            Text = "0:00",
            FontSize = 11,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = CreateTimeBrush()
        };

        _remainingText = new TextBlock
        {
            Text = "-0:00",
            FontSize = 11,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = CreateTimeBrush(),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var timeRow = new Grid();
        timeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_positionText, 0);
        Grid.SetColumn(_remainingText, 1);
        timeRow.Children.Add(_positionText);
        timeRow.Children.Add(_remainingText);

        section.Children.Add(_progressTrack);
        section.Children.Add(timeRow);
        return section;
    }

    private UIElement BuildTransportSection()
    {
        _previousButton = CreateRoundTransportButton(CreateTransportGlyph(TransportGlyph.Previous, CreateIconBrush()), 50, TransparentBrush, CreateIconBrush(), OnPreviousClick);

        _playPauseButton = CreateRoundTransportButton(CreatePlayPauseGlyph(_mediaService.IsPlaying), 50, TransparentBrush, CreateAccentBrush(), OnPlayPauseClick);

        _nextButton = CreateRoundTransportButton(CreateTransportGlyph(TransportGlyph.Next, CreateIconBrush()), 50, TransparentBrush, CreateIconBrush(), OnNextClick);

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12
        };
        panel.Children.Add(_previousButton);
        panel.Children.Add(_playPauseButton);
        panel.Children.Add(_nextButton);
        return panel;
    }

    private UIElement BuildVolumeSection()
    {
        var row = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lowSpeaker = CreateIconText("\uE992", 12, CreateIconMutedBrush());
        lowSpeaker.VerticalAlignment = VerticalAlignment.Center;
        lowSpeaker.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(lowSpeaker, 0);
        row.Children.Add(lowSpeaker);

        _volumeTrack = new Grid
        {
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent)
        };

        var volBg = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = CreateTrackBackgroundBrush(),
            VerticalAlignment = VerticalAlignment.Center
        };

        _volumeFill = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = CreateTrackFillBrush(),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 0
        };

        _volumeTrack.Children.Add(volBg);
        _volumeTrack.Children.Add(_volumeFill);

        _volumeTrack.SizeChanged += (_, e) =>
        {
            _volumeTrackWidth = e.NewSize.Width;
            UpdateVolumeVisual();
        };
        _volumeTrack.PointerPressed += OnVolumePointerPressed;
        _volumeTrack.PointerMoved += OnVolumePointerMoved;
        _volumeTrack.PointerReleased += OnVolumePointerReleased;
        _volumeTrack.PointerCaptureLost += OnVolumePointerCaptureLost;

        Grid.SetColumn(_volumeTrack, 1);
        row.Children.Add(_volumeTrack);

        var highSpeaker = CreateIconText("\uE995", 12, CreateIconMutedBrush());
        highSpeaker.VerticalAlignment = VerticalAlignment.Center;
        highSpeaker.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(highSpeaker, 2);
        row.Children.Add(highSpeaker);

        return row;
    }

    private FrameworkElement BuildSourceWindowSection()
    {
        _sourceWindowIcon = CreateIconText("\uE8A7", 14, CreateIconBrush());
        _sourceWindowLabel = new TextBlock
        {
            FontSize = 13,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            Foreground = CreateTitleBrush(),
            VerticalAlignment = VerticalAlignment.Center
        };

        var chevron = CreateIconText("\uE76C", 11, CreateIconMutedBrush());
        chevron.VerticalAlignment = VerticalAlignment.Center;

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        labelPanel.Children.Add(_sourceWindowIcon);
        labelPanel.Children.Add(_sourceWindowLabel);

        Grid.SetColumn(labelPanel, 0);
        Grid.SetColumnSpan(labelPanel, 2);
        Grid.SetColumn(chevron, 2);
        content.Children.Add(labelPanel);
        content.Children.Add(chevron);

        _sourceWindowButton = PanelButtonFactory.Create(
            content,
            TransparentBrush,
            CreateIconBrush(),
            TransparentBrush,
            TransparentBrush,
            OnSourceWindowToggleClick,
            height: 36,
            padding: new Thickness(12, 0, 12, 0),
            cornerRadius: new CornerRadius(18),
            horizontalAlignment: HorizontalAlignment.Stretch,
            horizontalContentAlignment: HorizontalAlignment.Stretch);
        return _sourceWindowButton;
    }

    private static TextBlock CreateIconText(string glyph, double size, Brush foreground)
    {
        return new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = size,
            Foreground = foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static Button CreateRoundTransportButton(
        UIElement content,
        double diameter,
        Brush background,
        Brush foreground,
        RoutedEventHandler handler)
    {
        var button = PanelButtonFactory.Create(
            content,
            background,
            foreground,
            background,
            background,
            handler,
            width: diameter,
            height: diameter,
            padding: new Thickness(0),
            cornerRadius: new CornerRadius(diameter / 2));
        button.MinWidth = 0;
        button.MinHeight = 0;
        button.PointerEntered += (_, _) =>
        {
            if (button.IsEnabled)
            {
                button.Opacity = 0.88;
            }
        };
        button.PointerExited += (_, _) =>
        {
            if (button.IsEnabled)
            {
                button.Opacity = 1;
            }
        };
        button.PointerPressed += (_, _) =>
        {
            if (button.IsEnabled)
            {
                button.Opacity = 0.72;
            }
        };
        button.PointerReleased += (_, _) =>
        {
            if (button.IsEnabled)
            {
                button.Opacity = 1;
            }
        };
        button.Click += handler;
        return button;
    }

    private UIElement CreatePlayPauseGlyph(bool isPlaying)
    {
        return isPlaying
            ? CreateTransportGlyph(TransportGlyph.Pause, CreateAccentBrush())
            : CreateTransportGlyph(TransportGlyph.Play, CreateAccentBrush());
    }

    private static UIElement CreateTransportGlyph(TransportGlyph glyph, Brush foreground)
    {
        var canvas = new Canvas
        {
            Width = 24,
            Height = 24
        };

        switch (glyph)
        {
            case TransportGlyph.Play:
                canvas.Children.Add(new Polygon
                {
                    Fill = foreground,
                    Points = new PointCollection
                    {
                        new(7, 4),
                        new(7, 20),
                        new(19, 12)
                    }
                });
                break;

            case TransportGlyph.Pause:
                canvas.Children.Add(new Rectangle
                {
                    Width = 5,
                    Height = 16,
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = foreground
                });
                Canvas.SetLeft(canvas.Children[^1], 6);
                Canvas.SetTop(canvas.Children[^1], 4);

                canvas.Children.Add(new Rectangle
                {
                    Width = 5,
                    Height = 16,
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = foreground
                });
                Canvas.SetLeft(canvas.Children[^1], 13);
                Canvas.SetTop(canvas.Children[^1], 4);
                break;

            case TransportGlyph.Previous:
                canvas.Children.Add(new Rectangle
                {
                    Width = 2,
                    Height = 16,
                    Fill = foreground
                });
                Canvas.SetLeft(canvas.Children[^1], 4);
                Canvas.SetTop(canvas.Children[^1], 4);

                canvas.Children.Add(new Polygon
                {
                    Fill = foreground,
                    Points = new PointCollection
                    {
                        new(18, 4),
                        new(18, 20),
                        new(8, 12)
                    }
                });
                break;

            case TransportGlyph.Next:
                canvas.Children.Add(new Rectangle
                {
                    Width = 2,
                    Height = 16,
                    Fill = foreground
                });
                Canvas.SetLeft(canvas.Children[^1], 18);
                Canvas.SetTop(canvas.Children[^1], 4);

                canvas.Children.Add(new Polygon
                {
                    Fill = foreground,
                    Points = new PointCollection
                    {
                        new(6, 4),
                        new(6, 20),
                        new(16, 12)
                    }
                });
                break;
        }

        return canvas;
    }

    private void OnProgressPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_progressTrackWidth <= 0) return;
        _isSeeking = true;
        _progressTrack?.CapturePointer(e.Pointer);
        UpdateSeekVisual(e);
    }

    private void OnProgressPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSeeking) return;
        UpdateSeekVisual(e);
    }

    private void OnProgressPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSeeking) return;
        CommitSeek(e);
        _progressTrack?.ReleasePointerCapture(e.Pointer);
    }

    private void OnProgressPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isSeeking = false;
    }

    private void UpdateSeekVisual(PointerRoutedEventArgs e)
    {
        if (_progressTrack is null || _progressFill is null || _positionText is null) return;
        var pos = e.GetCurrentPoint(_progressTrack).Position;
        double ratio = Math.Clamp(pos.X / _progressTrackWidth, 0, 1);
        _progressFill.Width = ratio * _progressTrackWidth;

        var duration = _mediaService.Duration;
        if (duration.TotalSeconds > 0)
        {
            var seekPos = TimeSpan.FromSeconds(duration.TotalSeconds * ratio);
            _positionText.Text = FormatTime(seekPos);
            if (_remainingText is not null)
            {
                _remainingText.Text = $"-{FormatTime(duration - seekPos)}";
            }
        }
    }

    private void CommitSeek(PointerRoutedEventArgs e)
    {
        _isSeeking = false;
        if (_progressTrack is null) return;

        var pos = e.GetCurrentPoint(_progressTrack).Position;
        double ratio = Math.Clamp(pos.X / _progressTrackWidth, 0, 1);
        var duration = _mediaService.Duration;
        if (duration.TotalSeconds > 0)
        {
            var target = TimeSpan.FromSeconds(duration.TotalSeconds * ratio);
            _ = _mediaService.SeekAsync(target);
        }
    }

    private void OnVolumePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_volumeTrackWidth <= 0) return;
        _isAdjustingVolume = true;
        _volumeTrack?.CapturePointer(e.Pointer);
        ApplyVolumeFromPointer(e);
    }

    private void OnVolumePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isAdjustingVolume) return;
        ApplyVolumeFromPointer(e);
    }

    private void OnVolumePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isAdjustingVolume) return;
        _isAdjustingVolume = false;
        _volumeTrack?.ReleasePointerCapture(e.Pointer);
    }

    private void OnVolumePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isAdjustingVolume = false;
    }

    private void ApplyVolumeFromPointer(PointerRoutedEventArgs e)
    {
        if (_volumeTrack is null || _volumeFill is null) return;
        var pos = e.GetCurrentPoint(_volumeTrack).Position;
        double ratio = Math.Clamp(pos.X / _volumeTrackWidth, 0, 1);
        _volumeFill.Width = ratio * _volumeTrackWidth;
        SystemVolumeHelper.SetVolume((float)ratio);
    }

    private void UpdateVolumeVisual()
    {
        if (_volumeFill is null || _volumeTrackWidth <= 0) return;
        float vol = SystemVolumeHelper.GetVolume();
        _volumeFill.Width = vol * _volumeTrackWidth;
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
        => _ = _mediaService.TogglePlayPauseAsync();

    private void OnNextClick(object sender, RoutedEventArgs e)
        => _ = _mediaService.NextAsync();

    private void OnPreviousClick(object sender, RoutedEventArgs e)
        => _ = _mediaService.PreviousAsync();

    private void OnShuffleClick(object sender, RoutedEventArgs e)
        => _ = _mediaService.ToggleShuffleAsync();

    private void OnRepeatClick(object sender, RoutedEventArgs e)
        => _ = _mediaService.CycleRepeatModeAsync();

    private void OnSourceWindowToggleClick(object sender, RoutedEventArgs e)
    {
        _sourceWindowService.ToggleVisibility(_mediaService.SourceAppId, _mediaService.SourceLabel);
        UpdateSourceWindowVisuals();
    }

    private void OnMediaStateChanged()
        => DispatcherQueue.TryEnqueue(UpdateUI);

    private void OnTimelineChanged()
        => DispatcherQueue.TryEnqueue(UpdateProgress);

    private void OnProgressTick(object? sender, object e)
    {
        if (!IsMenuVisible) return;
        UpdateProgress();
        UpdateVolumeVisual();
    }

    private void UpdateUI()
    {
        _sourceWindowService.SyncSource(_mediaService.SourceAppId);

        if (_titleText is not null)
            _titleText.Text = _mediaService.TrackTitle ?? "Not Playing";

        if (_artistText is not null)
            _artistText.Text = !string.IsNullOrWhiteSpace(_mediaService.TrackArtist)
                ? _mediaService.TrackArtist
                : _sourceWindowService.GetSourceLabel(_mediaService.SourceAppId, _mediaService.SourceLabel);

        if (_playPauseButton is not null)
            _playPauseButton.Content = CreatePlayPauseGlyph(_mediaService.IsPlaying);

        UpdateTransportVisuals();
        if (_settings.MusicShowSourceToggle)
        {
            UpdateSourceWindowVisuals();
        }
        UpdateProgress();
        if (_settings.MusicShowVolume)
        {
            UpdateVolumeVisual();
        }

        _ = LoadAlbumArtAsync();
    }

    private void UpdateTransportVisuals()
    {
        SetButtonEnabled(_previousButton, _mediaService.CanSkipPrevious);
        SetButtonEnabled(_playPauseButton, _mediaService.CanTogglePlayPause);
        SetButtonEnabled(_nextButton, _mediaService.CanSkipNext);
    }

    private void UpdateProgress()
    {
        if (_progressFill is null || _positionText is null || _remainingText is null) return;
        if (_isSeeking) return;

        var duration = _mediaService.Duration;
        var position = _mediaService.EstimatedPosition;

        _positionText.Text = FormatTime(position);
        _remainingText.Text = duration.TotalSeconds > 0
            ? $"-{FormatTime(duration - position)}"
            : "-0:00";

        if (_progressTrackWidth > 0 && duration.TotalSeconds > 0)
        {
            _progressFill.Width = (position.TotalSeconds / duration.TotalSeconds) * _progressTrackWidth;
        }
        else
        {
            _progressFill.Width = 0;
        }
    }

    private void UpdateSourceWindowVisuals()
    {
        if (_sourceWindowButton is null || _sourceWindowLabel is null || _sourceWindowIcon is null)
        {
            return;
        }

        string appLabel = _sourceWindowService.GetSourceLabel(_mediaService.SourceAppId, _mediaService.SourceLabel);
        bool canToggle = _sourceWindowService.IsSourceWindowHidden || !string.IsNullOrWhiteSpace(_mediaService.SourceAppId);

        _sourceWindowButton.IsEnabled = canToggle;
        _sourceWindowButton.Opacity = canToggle ? 1 : 0.42;

        if (_sourceWindowService.IsSourceWindowHidden)
        {
            _sourceWindowButton.Background = TransparentBrush;
            _sourceWindowIcon.Text = "\uE8A7";
            _sourceWindowIcon.Foreground = CreateAccentBrush();
            _sourceWindowLabel.Text = $"Reopen {appLabel}";
            _sourceWindowLabel.Foreground = CreateTitleBrush();
            return;
        }

        _sourceWindowButton.Background = TransparentBrush;
        _sourceWindowIcon.Text = "\uE711";
        _sourceWindowIcon.Foreground = CreateIconBrush();
        _sourceWindowLabel.Text = canToggle ? $"Hide {appLabel}" : "Source app unavailable";
        _sourceWindowLabel.Foreground = CreateTitleBrush();
    }

    private static void SetButtonEnabled(Button? button, bool enabled)
    {
        if (button is null)
        {
            return;
        }

        button.IsEnabled = enabled;
        button.Opacity = enabled ? 1 : 0.4;
    }

    private async Task LoadAlbumArtAsync()
    {
        int loadVersion = ++_albumArtLoadVersion;

        try
        {
            if (_coverArtBrush is not null)
            {
                var artwork = await MediaArtworkLoader.LoadVisualAsync(
                    _mediaService.ThumbnailRef,
                    _mediaService.SourceAppId,
                    _mediaService.TrackTitle,
                    _mediaService.TrackArtist,
                    _mediaService.AlbumTitle,
                    _mediaService.Subtitle);
                if (loadVersion != _albumArtLoadVersion)
                {
                    return;
                }

                ApplyArtwork(artwork.Source, artwork.AverageColor);
                return;
            }
        }
        catch { }

        if (loadVersion == _albumArtLoadVersion)
        {
            ApplyArtwork(null, global::Windows.UI.Color.FromArgb(255, 24, 24, 30));
        }
    }

    private void ApplyArtwork(ImageSource? imageSource, global::Windows.UI.Color averageColor)
    {
        if (_coverArtBrush is not null)
        {
            _coverArtBrush.ImageSource = imageSource;
        }

    }

    private static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }

    public void Initialize()
    {
        var appWindow = WindowHelper.GetAppWindow(this);
        appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(-9999, -9999, 1, 1));
        Activate();
    }

    public void ShowAt(int x, int y)
    {
        _mediaService.RefreshTimeline();
        PanelWindowMetrics metrics = BuildUI(x, y + 6);
        UpdateUI();
        UpdateVolumeVisual();

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
        _progressTimer.Start();
    }

    public void Hide()
    {
        _visibilityTimer.Stop();
        _progressTimer.Stop();
        ShowWindowNative(_hwnd, SW_HIDE);
        IsMenuVisible = false;
        LastHiddenAtUtc = DateTime.UtcNow;
    }

    public void RefreshLayout()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        SetupAcrylic();

        if (!IsMenuVisible)
        {
            BuildUI(0, 0);
            return;
        }

        if (!GetWindowRect(_hwnd, out var rect))
        {
            return;
        }

        PanelWindowMetrics metrics = BuildUI(rect.Right, rect.Top);

        var appWindow = WindowHelper.GetAppWindow(this);
        appWindow.MoveAndResize(metrics.Bounds);
        WindowHelper.ApplyRoundedRegion(_hwnd, metrics.Bounds.Width, metrics.Bounds.Height, PanelCornerRadius);
    }

    private SolidColorBrush CreateTitleBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(236, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(245, 255, 255, 255));
    }

    private SolidColorBrush CreateArtistBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(176, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(190, 255, 255, 255));
    }

    private SolidColorBrush CreateIconBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(228, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(238, 255, 255, 255));
    }

    private SolidColorBrush CreateIconMutedBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(128, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(125, 255, 255, 255));
    }

    private SolidColorBrush CreateAccentBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(224, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(235, 255, 255, 255));
    }

    private SolidColorBrush CreateTimeBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(156, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(140, 255, 255, 255));
    }

    private SolidColorBrush CreateTrackBackgroundBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(38, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(62, 255, 255, 255));
    }

    private SolidColorBrush CreateTrackFillBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(220, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(245, 255, 255, 255));
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
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd) return;
        Hide();
    }

}

internal enum TransportGlyph
{
    Play,
    Pause,
    Previous,
    Next
}
