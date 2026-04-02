using Veil.Services;
using Veil.Interop;
using Veil.Configuration;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinRT;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class WeatherWindow : Window
{
    private const int PanelWidth = 344;
    private const int MinimumPanelWidth = 300;
    private const int MinimumPanelHeight = 120;
    private const int PanelCornerRadius = 16;
    private const int ContentInset = 16;
    private const int PanelWidthPadding = 16;
    private const int PanelHeightPadding = 22;
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly DispatcherTimer _visibilityTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _loadingRetryTimer;
    private WeatherService _weatherService = null!;
    private IntPtr _hwnd;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private DateTime _openedAtUtc;
    private int _panelWidth = PanelWidth;
    private int _panelHeight = 420;

    public bool IsWeatherVisible { get; private set; }
    public DateTime LastHiddenAtUtc { get; private set; }

    public WeatherWindow()
    {
        InitializeComponent();
        Title = "Weather";

        _visibilityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _visibilityTimer.Tick += OnVisibilityTick;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _refreshTimer.Tick += OnRefreshTick;

        _loadingRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _loadingRetryTimer.Tick += OnLoadingRetryTick;

        Activated += OnFirstActivated;
        Activated += OnWindowActivated;
        Closed += OnClosed;
        _settings.Changed += OnSettingsChanged;
    }

    private bool UseLightTheme => _settings.TopBarPanelTheme == "Light";

    internal void SetWeatherService(WeatherService service)
    {
        if (_weatherService == service)
        {
            return;
        }

        if (_weatherService is not null)
        {
            _weatherService.StateChanged -= OnWeatherStateChanged;
        }

        _weatherService = service;
        _weatherService.StateChanged += OnWeatherStateChanged;
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
        BuildContent(0, 0);
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
            TintOpacity = UseLightTheme ? 0.06f : 0.68f,
            LuminosityOpacity = UseLightTheme ? 0.78f : 0.24f,
            FallbackColor = UseLightTheme
                ? global::Windows.UI.Color.FromArgb(216, 255, 255, 255)
                : global::Windows.UI.Color.FromArgb(236, 0, 0, 0)
        };

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = UseLightTheme ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark
        };

        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
    }

    public void Initialize()
    {
        var appWindow = WindowHelper.GetAppWindow(this);
        appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(-9999, -9999, 1, 1));
        Activate();
    }

    public void ShowAt(int x, int y)
    {
        var metrics = BuildContent(x, y + 6);

        var appWindow = WindowHelper.GetAppWindow(this);
        appWindow.MoveAndResize(metrics.Bounds);
        WindowHelper.ApplyRoundedRegion(_hwnd, metrics.Bounds.Width, metrics.Bounds.Height, PanelCornerRadius);

        appWindow.Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        Activate();
        SetForegroundWindow(_hwnd);

        IsWeatherVisible = true;
        _openedAtUtc = DateTime.UtcNow;
        _visibilityTimer.Start();
        _refreshTimer.Start();
        _ = _weatherService.RefreshAsync(true);
        UpdateLoadingRetryState();
    }

    public void Hide()
    {
        _visibilityTimer.Stop();
        _refreshTimer.Stop();
        _loadingRetryTimer.Stop();
        ShowWindowNative(_hwnd, SW_HIDE);
        IsWeatherVisible = false;
        LastHiddenAtUtc = DateTime.UtcNow;
    }

    private void OnWeatherStateChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!GetWindowRect(_hwnd, out var rect))
            {
                BuildContent(0, 0);
                return;
            }

            var metrics = BuildContent(rect.Right, rect.Top);
            if (!IsWeatherVisible)
            {
                return;
            }

            var appWindow = WindowHelper.GetAppWindow(this);
            appWindow.MoveAndResize(metrics.Bounds);
            WindowHelper.ApplyRoundedRegion(_hwnd, metrics.Bounds.Width, metrics.Bounds.Height, PanelCornerRadius);
            UpdateLoadingRetryState();
        });
    }

    private void OnRefreshTick(object? sender, object e)
    {
        if (!IsWeatherVisible)
        {
            return;
        }

        _ = _weatherService.RefreshAsync(true);
    }

    private void OnLoadingRetryTick(object? sender, object e)
    {
        if (!IsWeatherVisible)
        {
            _loadingRetryTimer.Stop();
            return;
        }

        if (_weatherService.Snapshot is not null)
        {
            _loadingRetryTimer.Stop();
            return;
        }

        _ = _weatherService.RefreshAsync(true);
    }

    private void OnSettingsChanged()
    {
        DispatcherQueue.TryEnqueue(RefreshAppearance);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!IsWeatherVisible) return;
        if (args.WindowActivationState != WindowActivationState.Deactivated) return;
        if ((DateTime.UtcNow - _openedAtUtc).TotalMilliseconds < 180) return;
        Hide();
    }

    private void OnVisibilityTick(object? sender, object e)
    {
        if (!IsWeatherVisible) return;
        if ((DateTime.UtcNow - _openedAtUtc).TotalMilliseconds < 220) return;
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _hwnd) return;
        Hide();
    }

    private PanelWindowMetrics BuildContent(int anchorRight, int anchorY)
    {
        ContentPanel.Children.Clear();

        var shell = new Border
        {
            Background = CreatePanelBackgroundBrush(),
            CornerRadius = new CornerRadius(PanelCornerRadius),
            Padding = new Thickness(ContentInset),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var layout = new StackPanel
        {
            Spacing = 12
        };

        var snapshot = _weatherService?.Snapshot;
        if (snapshot is null)
        {
            layout.Children.Add(new TextBlock
            {
                Text = "Weather",
                FontSize = 13,
                FontFamily = (FontFamily)Application.Current.Resources["SfDisplaySemibold"],
                Foreground = CreatePrimaryTextBrush()
            });
            layout.Children.Add(new TextBlock
            {
                Text = "Loading forecast...",
                FontSize = 12,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
                Foreground = CreateSecondaryTextBrush()
            });

            shell.Child = layout;
            PanelWindowMetrics emptyMetrics = PanelWindowSizer.Measure(
                shell,
                anchorRight,
                anchorY,
                PanelWidth,
                MinimumPanelWidth,
                MinimumPanelHeight,
                PanelWidthPadding,
                PanelHeightPadding);
            if (emptyMetrics.IsHeightClamped)
            {
                shell.Child = CreateScrollableContent(layout);
                shell.Width = emptyMetrics.Width;
                shell.MaxWidth = emptyMetrics.Width;
                shell.Height = emptyMetrics.Height;
            }
            _panelWidth = emptyMetrics.Width;
            _panelHeight = emptyMetrics.Height;
            ContentPanel.Children.Add(shell);
            return emptyMetrics;
        }

        layout.Children.Add(BuildHeader(snapshot.PrimaryCity));
        layout.Children.Add(BuildPrimaryMetricsSection(snapshot.PrimaryCity));
        layout.Children.Add(CreateSeparator());
        layout.Children.Add(BuildHourlySection(snapshot.HourlyForecast));
        if (snapshot.OtherCities.Count > 0)
        {
            layout.Children.Add(CreateSeparator());
            layout.Children.Add(BuildCitiesSection(snapshot.OtherCities));
        }

        shell.Child = layout;
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
            shell.Child = CreateScrollableContent(layout);
            shell.Width = metrics.Width;
            shell.MaxWidth = metrics.Width;
            shell.Height = metrics.Height;
        }
        _panelWidth = metrics.Width;
        _panelHeight = metrics.Height;
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

    private UIElement BuildHeader(WeatherCityForecast city)
    {
        var grid = new Grid
        {
            ColumnSpacing = 12
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textPanel = new StackPanel
        {
            Spacing = 1
        };
        textPanel.Children.Add(new TextBlock
        {
            Text = city.DisplayName,
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
            Foreground = CreateSecondaryTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = $"{Math.Round(city.TemperatureC):0}°",
            FontSize = 38,
            FontFamily = (FontFamily)Application.Current.Resources["SfDisplaySemibold"],
            Foreground = CreatePrimaryTextBrush(),
            LineHeight = 40
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = WeatherService.GetConditionLabel(city.WeatherCode),
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            Foreground = CreateSecondaryTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = $"Updated {DateTime.Now:HH:mm}",
            FontSize = 11,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = CreateTertiaryTextBrush()
        });

        var summaryPanel = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        summaryPanel.Children.Add(WeatherVisualFactory.CreateIcon(
            city.WeatherCode,
            city.IsDay,
            26,
            UseLightTheme));
        summaryPanel.Children.Add(new TextBlock
        {
            Text = $"H {Math.Round(city.HighTemperatureC):0}°  L {Math.Round(city.LowTemperatureC):0}°",
            FontSize = 11,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
            Foreground = CreatePrimaryTextBrush(),
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right
        });

        Grid.SetColumn(textPanel, 0);
        Grid.SetColumn(summaryPanel, 1);
        grid.Children.Add(textPanel);
        grid.Children.Add(summaryPanel);
        return grid;
    }

    private UIElement BuildPrimaryMetricsSection(WeatherCityForecast city)
    {
        var grid = new Grid
        {
            ColumnSpacing = 8,
            RowSpacing = 8
        };

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddMetricCard(grid, 0, 0, "Feels", $"{Math.Round(city.ApparentTemperatureC):0}°");
        AddMetricCard(grid, 1, 0, "Humidity", $"{city.HumidityPercent}%");
        AddMetricCard(grid, 0, 1, "Wind", $"{Math.Round(city.WindSpeedKph):0} km/h");
        AddMetricCard(grid, 1, 1, "Sun", city.IsDay ? $"Set {city.SunsetLocal:HH:mm}" : $"Rise {city.SunriseLocal:HH:mm}");

        return grid;
    }

    private void AddMetricCard(Grid grid, int column, int row, string label, string value)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 10),
            Background = CreateCardBackgroundBrush()
        };

        var stack = new StackPanel
        {
            Spacing = 2
        };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
            Foreground = CreateTertiaryTextBrush()
        });
        stack.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 14,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
            Foreground = CreatePrimaryTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        });

        card.Child = stack;
        Grid.SetColumn(card, column);
        Grid.SetRow(card, row);
        grid.Children.Add(card);
    }

    private UIElement BuildHourlySection(IReadOnlyList<WeatherHourlyForecast> hourlyForecast)
    {
        var wrapper = new StackPanel
        {
            Spacing = 8
        };

        wrapper.Children.Add(new TextBlock
        {
            Text = "Next hours",
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
            Foreground = CreateSecondaryTextBrush()
        });

        var visibleForecast = hourlyForecast.Take(5).ToList();
        var panel = new Grid
        {
            ColumnSpacing = 8
        };

        for (int i = 0; i < visibleForecast.Count; i++)
        {
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            WeatherHourlyForecast forecast = visibleForecast[i];
            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8, 10, 8, 10),
                Background = CreateCardBackgroundBrush()
            };

            var cell = new StackPanel
            {
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            cell.Children.Add(new TextBlock
            {
                Text = forecast.Time.ToString("htt", System.Globalization.CultureInfo.InvariantCulture).Replace("AM", "AM").Replace("PM", "PM"),
                FontSize = 11,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
                Foreground = CreateSecondaryTextBrush(),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            cell.Children.Add(WeatherVisualFactory.CreateIcon(
                forecast.WeatherCode,
                forecast.IsDay,
                18,
                UseLightTheme));
            cell.Children.Add(new TextBlock
            {
                Text = $"{Math.Round(forecast.TemperatureC):0}°",
                FontSize = 13,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
                Foreground = CreatePrimaryTextBrush(),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            cell.Children.Add(new TextBlock
            {
                Text = $"{forecast.PrecipitationProbabilityPercent}%",
                FontSize = 10,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
                Foreground = forecast.PrecipitationProbabilityPercent > 0
                    ? CreateAccentTextBrush()
                    : CreateTertiaryTextBrush(),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            card.Child = cell;
            Grid.SetColumn(card, i);
            panel.Children.Add(card);
        }

        wrapper.Children.Add(panel);
        return wrapper;
    }

    private UIElement BuildCitiesSection(IReadOnlyList<WeatherCityForecast> cities)
    {
        var stack = new StackPanel
        {
            Spacing = 8
        };

        stack.Children.Add(new TextBlock
        {
            Text = "Other cities",
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
            Foreground = CreateSecondaryTextBrush()
        });

        foreach (WeatherCityForecast city in cities)
        {
            var rowCard = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 10, 12, 10),
                Background = CreateCardBackgroundBrush()
            };

            var row = new Grid
            {
                ColumnSpacing = 8
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });

            var cityText = new StackPanel
            {
                Spacing = 1
            };
            cityText.Children.Add(new TextBlock
            {
                Text = city.DisplayName,
                FontSize = 13,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
                Foreground = CreatePrimaryTextBrush(),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            });
            cityText.Children.Add(new TextBlock
            {
                Text = WeatherService.GetConditionLabel(city.WeatherCode),
                FontSize = 10,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
                Foreground = CreateTertiaryTextBrush(),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            });
            row.Children.Add(cityText);

            var icon = WeatherVisualFactory.CreateIcon(city.WeatherCode, city.IsDay, 16, UseLightTheme);
            Grid.SetColumn(icon, 1);
            if (icon is FrameworkElement iconElement)
            {
                iconElement.Margin = new Thickness(0);
            }

            row.Children.Add(icon);

            var temp = new TextBlock
            {
                Text = $"{Math.Round(city.TemperatureC):0}° · H {Math.Round(city.HighTemperatureC):0}°",
                FontSize = 11,
                FontFamily = (FontFamily)Application.Current.Resources["SfTextSemibold"],
                Foreground = CreatePrimaryTextBrush(),
                HorizontalAlignment = HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            };
            Grid.SetColumn(temp, 2);
            row.Children.Add(temp);

            rowCard.Child = row;
            stack.Children.Add(rowCard);
        }

        return stack;
    }

    private Rectangle CreateSeparator()
    {
        return new Rectangle
        {
            Height = 1,
            Fill = UseLightTheme
                ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(28, 0, 0, 0))
                : new SolidColorBrush(global::Windows.UI.Color.FromArgb(24, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    internal void RefreshAppearance()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        SetupAcrylic();

        if (!GetWindowRect(_hwnd, out var rect))
        {
            BuildContent(0, 0);
            return;
        }

        var metrics = BuildContent(rect.Right, rect.Top);

        if (!IsWeatherVisible)
        {
            return;
        }

        var appWindow = WindowHelper.GetAppWindow(this);
        appWindow.MoveAndResize(metrics.Bounds);
        WindowHelper.ApplyRoundedRegion(_hwnd, metrics.Bounds.Width, metrics.Bounds.Height, PanelCornerRadius);
    }

    private SolidColorBrush CreatePanelBackgroundBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(38, 255, 255, 255))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(34, 10, 10, 10));
    }

    private SolidColorBrush CreateCardBackgroundBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(24, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(24, 255, 255, 255));
    }

    private SolidColorBrush CreatePrimaryTextBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(230, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(234, 255, 255, 255));
    }

    private SolidColorBrush CreateSecondaryTextBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(180, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(170, 255, 255, 255));
    }

    private SolidColorBrush CreateTertiaryTextBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(132, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(128, 255, 255, 255));
    }

    private SolidColorBrush CreateAccentTextBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(220, 21, 94, 196))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(220, 122, 184, 255));
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _settings.Changed -= OnSettingsChanged;
    }

    private void UpdateLoadingRetryState()
    {
        if (IsWeatherVisible && _weatherService.Snapshot is null)
        {
            _loadingRetryTimer.Start();
            return;
        }

        _loadingRetryTimer.Stop();
    }
}
