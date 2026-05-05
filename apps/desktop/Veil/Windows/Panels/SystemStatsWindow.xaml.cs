using System.Diagnostics;
using System.Net.NetworkInformation;
using Veil.Configuration;
using Veil.Interop;
using Veil.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinRT;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class SystemStatsWindow : Window
{
    private const int PanelWidth = 820;
    private const int MultiGpuPanelWidthStep = 80;
    private const int MaximumPanelWidth = 1080;
    private const int MinimumPanelHeight = 320;
    private const int PanelCornerRadius = 12;
    private const int ScreenMargin = 8;
    private const int HorizontalContentInset = 20;
    private const int DashboardColumnCount = 5;
    private const double DashboardColumnSpacing = 6;
    private static readonly string[] ProcessPalette =
    [
        "#5E9BFF",
        "#8FD694",
        "#F5B85B",
        "#B69CFF",
        "#F28BA8"
    ];

    private readonly AppSettings _settings = AppSettings.Current;
    private IntPtr _hwnd;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _visibilityTimer;
    private DateTime _openedAtUtc;
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _cpuUserCounter;
    private PerformanceCounter? _cpuSystemCounter;
    private string? _cpuName;
    private DateTime _lastVeilCpuSampleUtc;
    private TimeSpan _lastVeilCpuTime;
    private DateTime _lastProcessCpuSampleUtc;
    private double _panelViewWidth = PanelWidth;
    private int _lastGpuCount;
    private DateTime _lastNetworkSampleUtc;
    private long _lastNetworkReceivedBytes;
    private long _lastNetworkSentBytes;
    private Dictionary<int, TimeSpan> _lastProcessCpuTimes = [];
    private IReadOnlyList<CpuProcessUsage> _cachedTopCpuProcesses = [];
    private IReadOnlyList<MemoryProcessUsage> _cachedTopMemProcesses = [];
    private int _processRefreshInFlight;
    private readonly Dictionary<string, Queue<double>> _historyByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<(double Up, double Down)>> _networkHistoryByKey = new(StringComparer.OrdinalIgnoreCase);

    public bool IsStatsVisible { get; private set; }
    public DateTime LastHiddenAtUtc { get; private set; }

    public SystemStatsWindow()
    {
        InitializeComponent();
        Title = "System Stats";

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _refreshTimer.Tick += OnRefreshTick;

        _visibilityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _visibilityTimer.Tick += OnVisibilityTick;

        Activated += OnFirstActivated;
        Activated += OnWindowActivated;
        Closed += OnClosed;
        _settings.Changed += OnSettingsChanged;
        WindowsProfileStore.Current.Changed += OnProfileStoreChanged;

        Task.Run(InitHardwareInfo);
    }

    private bool UseLightTheme => _settings.RunCatPanelTheme == "Light";

    private void InitHardwareInfo()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
            _cpuCounter.NextValue();
            _cpuUserCounter = new PerformanceCounter("Processor Information", "% User Time", "_Total");
            _cpuSystemCounter = new PerformanceCounter("Processor Information", "% Privileged Time", "_Total");
            _cpuUserCounter.NextValue();
            _cpuSystemCounter.NextValue();
        }
        catch
        {
            _cpuCounter = null;
            _cpuUserCounter = null;
            _cpuSystemCounter = null;
        }

        try
        {
            _cpuName = GetCpuName();
        }
        catch
        {
        }
    }

    private static string? GetCpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            string? name = key?.GetValue("ProcessorNameString") as string;
            return name?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        _hwnd = WindowHelper.GetHwnd(this);
        ShowWindowNative(_hwnd, SW_HIDE);
        WindowHelper.RemoveTitleBar(this);

        int exStyle = GetWindowLongW(_hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        exStyle &= ~WS_EX_NOACTIVATE;
        SetWindowLongW(_hwnd, GWL_EXSTYLE, exStyle);

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        WindowHelper.PrepareForSystemBackdrop(this);
        SetupAcrylic();
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
            FallbackColor = PanelGlassPalette.GetEffectiveFallbackColor(UseLightTheme)
        };

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = PanelGlassPalette.GetBackdropTheme(UseLightTheme)
        };

        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
        PanelBorder.Background = WindowHelper.IsWindowsTransparencyEnabled()
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0))
            : new SolidColorBrush(WindowHelper.GetOpaqueThemeBackgroundColor());
    }

    public void Initialize()
    {
        var hwnd = WindowHelper.GetHwnd(this);
        int cloak = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));

        Activate();

        ShowWindowNative(hwnd, SW_HIDE);
        cloak = 0;
        DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
    }

    public void ShowAt(int x, int y)
    {
        int panelWidth = GetTargetPanelWidth(x, y + 6);
        _panelViewWidth = Math.Max(1, WindowHelper.PhysicalPixelsToView(this, panelWidth));

        StartProcessRefreshAsync();
        BuildAndRefresh();

        int panelHeight = CalculateHeight();
        var appWindow = WindowHelper.GetAppWindow(this);
        var bounds = ResolveWindowBounds(x - panelWidth, y + 6, panelWidth, panelHeight);
        ApplyPanelSize(bounds.Width, bounds.Height);
        appWindow.MoveAndResize(bounds);
        WindowHelper.ApplyRoundedRegion(_hwnd, bounds.Width, bounds.Height, PanelCornerRadius);

        appWindow.Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        Activate();
        SetForegroundWindow(_hwnd);

        IsStatsVisible = true;
        _openedAtUtc = DateTime.UtcNow;
        _visibilityTimer.Start();
        _refreshTimer.Start();
    }

    private Border CreateMetricCard(double minHeight, double padding = 9)
    {
        return new Border
        {
            MinHeight = minHeight,
            Padding = new Thickness(padding),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0)
        };
    }

    private TextBlock CreateMetricTitle(string title)
    {
        return new TextBlock
        {
            Text = title,
            FontSize = 10,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            Foreground = CreateLabelBrush(154, 132),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
    }

    private SolidColorBrush CreateCardBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(142, 255, 255, 255))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(118, 45, 49, 56));
    }

    private SolidColorBrush CreateCardBorderBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(76, 255, 255, 255))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(34, 255, 255, 255));
    }

    private SolidColorBrush CreateInsetBrush(global::Windows.UI.Color accent, byte darkAlpha = 22, byte lightAlpha = 18)
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(lightAlpha, accent.R, accent.G, accent.B))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(darkAlpha, accent.R, accent.G, accent.B));
    }

    public void Hide()
    {
        _visibilityTimer.Stop();
        _refreshTimer.Stop();
        ShowWindowNative(_hwnd, SW_HIDE);
        IsStatsVisible = false;
        LastHiddenAtUtc = DateTime.UtcNow;
    }

    private void OnSettingsChanged()
    {
        DispatcherQueue.TryEnqueue(RefreshAppearance);
    }

    private void OnProfileStoreChanged()
    {
        DispatcherQueue.TryEnqueue(RefreshAppearance);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _settings.Changed -= OnSettingsChanged;
        WindowsProfileStore.Current.Changed -= OnProfileStoreChanged;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!IsStatsVisible) return;
        if (args.WindowActivationState != WindowActivationState.Deactivated) return;
        if ((DateTime.UtcNow - _openedAtUtc).TotalMilliseconds < 180) return;
        Hide();
    }

    private void OnVisibilityTick(object? sender, object e)
    {
        if (!IsStatsVisible) return;
        if ((DateTime.UtcNow - _openedAtUtc).TotalMilliseconds < 220) return;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd) return;
        Hide();
    }

    private void OnRefreshTick(object? sender, object e)
    {
        if (!IsStatsVisible) return;

        var appWindow = WindowHelper.GetAppWindow(this);
        var pos = appWindow.Position;
        int panelWidth = GetTargetPanelWidth(pos.X, pos.Y);
        _panelViewWidth = Math.Max(1, WindowHelper.PhysicalPixelsToView(this, panelWidth));

        StartProcessRefreshAsync();
        BuildAndRefresh();

        int panelHeight = CalculateHeight();
        var bounds = ResolveWindowBounds(pos.X, pos.Y, panelWidth, panelHeight);
        ApplyPanelSize(bounds.Width, bounds.Height);
        appWindow.MoveAndResize(bounds);
        WindowHelper.ApplyRoundedRegion(_hwnd, bounds.Width, bounds.Height, PanelCornerRadius);
    }

    private int CalculateHeight()
    {
        StatsPanel.Measure(new global::Windows.Foundation.Size(Math.Max(1, _panelViewWidth - HorizontalContentInset), double.PositiveInfinity));
        double desiredHeight = Math.Ceiling(StatsPanel.DesiredSize.Height + 16);
        return Math.Max(1, WindowHelper.ViewPixelsToPhysical(this, Math.Max(MinimumPanelHeight, desiredHeight)));
    }

    private void BuildAndRefresh()
    {
        StatsPanel.Children.Clear();

        AddProfileSummary();

        var memoryStatus = MemoryStatusEx.Create();
        _ = GlobalMemoryStatusEx(ref memoryStatus);
        var gpus = GraphicsMemoryMonitor.GetAllGpuInfo();
        _lastGpuCount = gpus.Count;
        NetworkStats networkStats = GetNetworkStats();
        _ = TryGetVeilStats(memoryStatus, out VeilStats veilStats);

        AddDashboardGrid(
        [
            CreateCpuColumn(),
            CreateGpuColumn(gpus),
            CreateRamColumn(memoryStatus),
            CreateNetworkColumn(networkStats),
            CreateVeilColumn(veilStats)
        ]);
    }

    private MetricColumnData CreateCpuColumn()
    {
        double cpuPercent = GetCpuUsage();
        double userPercent = GetCpuUserUsage();
        double systemPercent = GetCpuSystemUsage();
        int coreCount = Environment.ProcessorCount;
        string model = _cpuName is not null ? ShortenCpuName(_cpuName) : "Processor";
        double idlePercent = Math.Clamp(100 - cpuPercent, 0, 100);
        double? cpuTemperature = HardwareSensorMonitor.GetSnapshot().CpuTemperatureCelsius;
        var details = new List<MetricDetail>
        {
            new("Model", model),
            new("Threads", coreCount.ToString())
        };

        if (cpuTemperature.HasValue)
        {
            details.Add(new MetricDetail("Temp", FormatTemperature(cpuTemperature.Value)));
        }

        details.Add(new MetricDetail("System", $"{systemPercent:F0}%"));
        details.Add(new MetricDetail("User", $"{userPercent:F0}%"));
        details.Add(new MetricDetail("Idle", $"{idlePercent:F0}%"));

        return new MetricColumnData(
            "CPU",
            $"{cpuPercent:F0}%",
            cpuPercent,
            "#6EA8FF",
            "cpu",
            details);
    }

    private MetricColumnData CreateGpuColumn(IReadOnlyList<GraphicsMemoryMonitor.GpuInfo> gpus)
    {
        if (gpus.Count == 0)
        {
            return new MetricColumnData(
                "GPU",
                "—",
                0,
                "#A997D8",
                "gpu",
                [
                    new MetricDetail("Status", "No telemetry available"),
                    new MetricDetail("Model", "Unavailable"),
                    new MetricDetail("Temperature", "Not exposed"),
                    new MetricDetail("VRAM", "Unavailable")
                ]);
        }

        var primaryGpu = gpus[0];
        double primaryPercent = primaryGpu.EngineUsagePercent > 0
            ? primaryGpu.EngineUsagePercent
            : primaryGpu.MemoryUsagePercent;
        var details = new List<MetricDetail>
        {
            new("Model", ShortenGpuName(primaryGpu.Name)),
            new("Temperature", primaryGpu.TemperatureCelsius.HasValue
                ? FormatTemperature(primaryGpu.TemperatureCelsius.Value)
                : "Not exposed")
        };

        if (primaryGpu.TotalBytes > 0)
        {
            details.Add(new MetricDetail("VRAM", FormatGpuMemory(primaryGpu.UsedBytes, primaryGpu.TotalBytes)));
        }
        else
        {
            details.Add(new MetricDetail("VRAM", "Unavailable"));
        }

        foreach (var gpu in gpus.Skip(1).Take(3))
        {
            double percent = gpu.EngineUsagePercent > 0 ? gpu.EngineUsagePercent : gpu.MemoryUsagePercent;
            string label = gpu.IsIntegrated ? "iGPU" : "GPU";
            details.Add(new MetricDetail(label, $"{percent:F0}%  {ShortenGpuName(gpu.Name)}"));
        }

        return new MetricColumnData(
            "GPU",
            $"{primaryPercent:F0}%",
            primaryPercent,
            "#A997D8",
            "gpu",
            details);
    }

    private MetricColumnData CreateRamColumn(MemoryStatusEx memoryStatus)
    {
        if (memoryStatus.ullTotalPhys == 0)
        {
            return new MetricColumnData(
                "RAM",
                "—",
                0,
                "#95D29B",
                "ram",
                [new MetricDetail("Status", "Unavailable")]);
        }

        double usedBytes = memoryStatus.ullTotalPhys - memoryStatus.ullAvailPhys;
        double percent = usedBytes / memoryStatus.ullTotalPhys * 100;
        double totalGb = memoryStatus.ullTotalPhys / (1024.0 * 1024 * 1024);
        double usedGb = usedBytes / (1024.0 * 1024 * 1024);
        double freeGb = memoryStatus.ullAvailPhys / (1024.0 * 1024 * 1024);

        return new MetricColumnData(
            "RAM",
            $"{percent:F0}%",
            percent,
            "#95D29B",
            "ram",
            [
                new MetricDetail("Total", $"{totalGb:F2} GB"),
                new MetricDetail("Used", $"{usedGb:F2} GB"),
                new MetricDetail("App", $"{usedGb:F2} GB"),
                new MetricDetail("Wired", "Not exposed"),
                new MetricDetail("Compressed", "Not exposed"),
                new MetricDetail("Free", $"{freeGb:F2} GB"),
                new MetricDetail("Pressure", GetPressureLabel(percent)),
            ]);
    }

    private MetricColumnData CreateNetworkColumn(NetworkStats stats)
    {
        return new MetricColumnData(
            "Network",
            stats.Value,
            stats.Percent,
            "#7FC8D8",
            "network",
            [
                new MetricDetail("Download", FormatByteRate(stats.ReceivedBytesPerSecond)),
                new MetricDetail("Upload", FormatByteRate(stats.SentBytesPerSecond)),
                new MetricDetail("Download Raw", stats.ReceivedBytesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new MetricDetail("Upload Raw", stats.SentBytesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new MetricDetail("Download Percent", $"{GetNetworkActivityPercent(stats.ReceivedBytesPerSecond):F0}%"),
                new MetricDetail("Upload Percent", $"{GetNetworkActivityPercent(stats.SentBytesPerSecond):F0}%"),
                new MetricDetail("Public IP", "Not fetched"),
                new MetricDetail("Interface", stats.InterfaceName),
                new MetricDetail("Local IP", stats.LocalIp),
                new MetricDetail("Network", stats.NetworkName),
                new MetricDetail("Physical address", stats.PhysicalAddress)
            ]);
    }

    private MetricColumnData CreateVeilColumn(VeilStats stats)
    {
        string impact = stats.CpuPercent > 12 || stats.MemoryPercent > 8
            ? "High"
            : stats.CpuPercent > 4 || stats.MemoryPercent > 3 ? "Moderate" : "Low";
        double impactPercent = impact == "High" ? 86 : impact == "Moderate" ? 52 : 18;

        return new MetricColumnData(
            "Veil",
            impact,
            impactPercent,
            "#D69AB4",
            "veil",
            [
                new MetricDetail("CPU", $"{stats.CpuPercent:F1}%"),
                new MetricDetail("RAM", FormatBytes(stats.WorkingSetBytes)),
                new MetricDetail("Threads", stats.ThreadCount.ToString()),
                new MetricDetail("Handles", stats.HandleCount.ToString()),
                new MetricDetail("Topbar", "Active"),
                new MetricDetail("RunCat", _settings.RunCatEnabled ? "Active" : "Off"),
                new MetricDetail("Profiles", WindowsProfileStore.Current.ActiveProfileId is not null ? "Active" : "Base"),
                new MetricDetail("Raycast", "Ready"),
                new MetricDetail("Media", _settings.MusicButtonEnabled ? "Ready" : "Off"),
                new MetricDetail("Discord", _settings.DiscordButtonEnabled ? "Ready" : "Off")
            ]);
    }

    private void AddDashboardGrid(IReadOnlyList<MetricColumnData> columns)
    {
        var grid = new Grid
        {
            ColumnSpacing = DashboardColumnSpacing,
            Margin = new Thickness(0, 1, 0, 0)
        };

        for (int i = 0; i < columns.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            FrameworkElement column = CreateMetricColumn(columns[i]);
            Grid.SetColumn(column, i);
            grid.Children.Add(column);
        }

        StatsPanel.Children.Add(grid);
    }

    private FrameworkElement CreateMetricColumn(MetricColumnData data)
    {
        global::Windows.UI.Color accent = ParseHexColor(data.AccentHex);
        var accentBrush = new SolidColorBrush(accent);
        double columnWidth = Math.Max(86, (_panelViewWidth - HorizontalContentInset - (DashboardColumnSpacing * (DashboardColumnCount - 1))) / DashboardColumnCount);

        if (data.HistoryKey == "cpu")
        {
            return CreateCpuMetricColumn(data, accent, accentBrush, columnWidth);
        }

        if (data.HistoryKey == "ram")
        {
            return CreateRamMetricColumn(data, accent, accentBrush, columnWidth);
        }

        if (data.HistoryKey == "network")
        {
            return CreateNetworkMetricColumn(data, accent, columnWidth);
        }

        var shell = CreateMetricCard(206, 8);

        var stack = new StackPanel { Spacing = 6 };

        var title = CreateMetricTitle(data.Title);

        var gauge = new Grid
        {
            Width = 60,
            Height = 60,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        gauge.Children.Add(new Ellipse
        {
            Width = 56,
            Height = 56,
            Stroke = CreateInsetBrush(accent, darkAlpha: 34, lightAlpha: 26),
            StrokeThickness = 4
        });
        gauge.Children.Add(CreateProgressRingArc(data.Percent, accent));
        gauge.Children.Add(new TextBlock
        {
            Text = data.Value,
            FontSize = data.Value.Length > 6 ? 10 : 12,
            FontFamily = (FontFamily)Application.Current.Resources["SfDisplaySemibold"],
            Foreground = CreateValueBrush(245, 42),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            TextWrapping = TextWrapping.WrapWholeWords,
            MaxWidth = 46
        });

        FrameworkElement sparkline = CreateSparkline(data.HistoryKey, data.Percent, accent, columnWidth - 16);
        var detailStack = new StackPanel { Spacing = 6 };
        foreach (MetricDetail detail in data.Details.Take(10))
        {
            detailStack.Children.Add(CreateDetailRow(detail));
        }

        stack.Children.Add(title);
        stack.Children.Add(gauge);
        stack.Children.Add(sparkline);
        stack.Children.Add(detailStack);
        shell.Child = stack;
        return shell;
    }

    private FrameworkElement CreateCpuMetricColumn(MetricColumnData data, global::Windows.UI.Color accent, SolidColorBrush accentBrush, double columnWidth)
    {
        var shell = CreateMetricCard(206, 8);

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(CreateMetricTitle("CPU"));

        var gauges = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            ColumnSpacing = 8
        };

        var temperatureGauge = CreateGauge("—°C", 0, global::Windows.UI.Color.FromArgb(255, 14, 165, 233), 38, 3, 9);
        IReadOnlyList<CpuProcessUsage> topProcesses = _cachedTopCpuProcesses;
        var usageGauge = CreateGauge(data.Value, data.Percent, accent, 60, 4, data.Value.Length > 6 ? 10 : 12, CreateCpuUsageSegments(topProcesses));
        Grid.SetColumn(usageGauge, 1);
        gauges.Children.Add(temperatureGauge);
        gauges.Children.Add(usageGauge);
        stack.Children.Add(gauges);

        stack.Children.Add(CreateSectionCaption("Usage history"));
        stack.Children.Add(CreateSparkline(data.HistoryKey, data.Percent, accent, columnWidth - 16));
        stack.Children.Add(CreateSectionCaption("Details"));

        double systemPercent = ParsePercentDetail(data.Details, "System");
        double userPercent = ParsePercentDetail(data.Details, "User");
        double idlePercent = ParsePercentDetail(data.Details, "Idle");
        stack.Children.Add(CreateCpuDetailRow("System", $"{systemPercent:F0}%", global::Windows.UI.Color.FromArgb(255, 239, 68, 68)));
        stack.Children.Add(CreateCpuDetailRow("User", $"{userPercent:F0}%", accent));
        stack.Children.Add(CreateCpuDetailRow("Idle", $"{idlePercent:F0}%", global::Windows.UI.Color.FromArgb(255, 148, 163, 184)));

        stack.Children.Add(CreateSectionCaption("Top processes"));
        if (topProcesses.Count == 0)
        {
            stack.Children.Add(CreateCpuProcessRow("Sampling", "—"));
        }
        else
        {
            foreach (CpuProcessUsage process in topProcesses.Take(5))
            {
                stack.Children.Add(CreateProcessUsageRow(process.Name, $"{process.Percent:F1}%", GetProcessColor(process.Name)));
            }
        }

        shell.Child = stack;
        return shell;
    }

    private FrameworkElement CreateGauge(
        string value,
        double percent,
        global::Windows.UI.Color accent,
        double size,
        double strokeThickness,
        double fontSize,
        IReadOnlyList<UsageSegment>? segments = null)
    {
        var gauge = new Grid
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        double ringSize = size - 4;
        gauge.Children.Add(new Ellipse
        {
            Width = ringSize,
            Height = ringSize,
            Stroke = CreateInsetBrush(accent, darkAlpha: 34, lightAlpha: 26),
            StrokeThickness = strokeThickness
        });
        if (segments is { Count: > 0 })
        {
            foreach (UIElement segment in CreateProgressRingSegments(segments, ringSize, strokeThickness))
            {
                gauge.Children.Add(segment);
            }
        }
        else
        {
            gauge.Children.Add(CreateProgressRingArc(percent, accent, ringSize, strokeThickness));
        }
        gauge.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = fontSize,
            FontFamily = (FontFamily)Application.Current.Resources["SfDisplaySemibold"],
            Foreground = CreateValueBrush(245, 42),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            TextWrapping = TextWrapping.WrapWholeWords,
            MaxWidth = Math.Max(32, size - 18)
        });

        return gauge;
    }

    private FrameworkElement CreateSectionCaption(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 8,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            Foreground = CreateSubValueBrush(78, 104),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 3, 0, 0)
        };
    }

    private FrameworkElement CreateCpuDetailRow(string label, string value, global::Windows.UI.Color color)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 6
        };

        row.Children.Add(new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center
        });

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 9,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = CreateSubValueBrush(90, 116),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(labelText, 1);

        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 9,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            Foreground = CreateValueBrush(220, 42),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(valueText, 2);

        row.Children.Add(labelText);
        row.Children.Add(valueText);
        return row;
    }

    private FrameworkElement CreateCpuProcessRow(string name, string value)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8
        };

        row.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 9,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = CreateSubValueBrush(94, 118),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 9,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            Foreground = CreateValueBrush(220, 42),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(valueText, 1);
        row.Children.Add(valueText);
        return row;
    }

    private FrameworkElement CreateProcessUsageRow(string name, string value, global::Windows.UI.Color color)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 6
        };

        row.Children.Add(new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center
        });

        var labelText = new TextBlock
        {
            Text = name,
            FontSize = 9,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = CreateSubValueBrush(94, 118),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(labelText, 1);

        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 9,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            Foreground = CreateValueBrush(220, 42),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(valueText, 2);

        row.Children.Add(labelText);
        row.Children.Add(valueText);
        return row;
    }

    private FrameworkElement CreateRamMetricColumn(MetricColumnData data, global::Windows.UI.Color accent, SolidColorBrush accentBrush, double columnWidth)
    {
        var shell = CreateMetricCard(206, 8);

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(CreateMetricTitle("RAM"));

        var gauges = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            ColumnSpacing = 8
        };

        IReadOnlyList<MemoryProcessUsage> topProcesses = _cachedTopMemProcesses;
        double totalBytes = ParseFormattedByteValue(GetDetailValue(data.Details, "Total"));
        var pressureGauge = CreateGauge(GetPressureLabel(data.Percent), data.Percent, global::Windows.UI.Color.FromArgb(255, 142, 181, 142), 38, 3, 8);
        var usageGauge = CreateGauge(data.Value, data.Percent, accent, 60, 4, data.Value.Length > 6 ? 10 : 12, CreateMemoryUsageSegments(topProcesses, totalBytes));
        Grid.SetColumn(usageGauge, 1);
        gauges.Children.Add(pressureGauge);
        gauges.Children.Add(usageGauge);
        stack.Children.Add(gauges);

        stack.Children.Add(CreateSectionCaption("Usage history"));
        stack.Children.Add(CreateSparkline(data.HistoryKey, data.Percent, accent, columnWidth - 16));
        stack.Children.Add(CreateSectionCaption("Details"));

        stack.Children.Add(CreateMemoryDetailRow("Total", GetDetailValue(data.Details, "Total"), global::Windows.UI.Color.FromArgb(255, 148, 163, 184)));
        stack.Children.Add(CreateMemoryDetailRow("Used", GetDetailValue(data.Details, "Used"), accent));
        stack.Children.Add(CreateMemoryDetailRow("App", GetDetailValue(data.Details, "App"), global::Windows.UI.Color.FromArgb(255, 59, 130, 246)));
        stack.Children.Add(CreateMemoryDetailRow("Wired", GetDetailValue(data.Details, "Wired"), global::Windows.UI.Color.FromArgb(255, 245, 158, 11)));
        stack.Children.Add(CreateMemoryDetailRow("Compressed", GetDetailValue(data.Details, "Compressed"), global::Windows.UI.Color.FromArgb(255, 244, 63, 94)));
        stack.Children.Add(CreateMemoryDetailRow("Free", GetDetailValue(data.Details, "Free"), global::Windows.UI.Color.FromArgb(255, 203, 213, 225)));

        stack.Children.Add(CreateSectionCaption("Top processes"));
        if (topProcesses.Count == 0)
        {
            stack.Children.Add(CreateCpuProcessRow("Sampling", "—"));
        }
        else
        {
            foreach (MemoryProcessUsage process in topProcesses.Take(5))
            {
                stack.Children.Add(CreateProcessUsageRow(process.Name, FormatBytes(process.WorkingSetBytes), GetProcessColor(process.Name)));
            }
        }

        shell.Child = stack;
        return shell;
    }

    private FrameworkElement CreateMemoryDetailRow(string label, string value, global::Windows.UI.Color color)
        => CreateCpuDetailRow(label, value, color);

    private FrameworkElement CreateNetworkMetricColumn(MetricColumnData data, global::Windows.UI.Color accent, double columnWidth)
    {
        var shell = CreateMetricCard(292, 8);

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(CreateMetricTitle("Network"));

        var rateGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            ColumnSpacing = 8
        };

        double uploadBytes = ParseByteRateDetail(data.Details, "Upload");
        double downloadBytes = ParseByteRateDetail(data.Details, "Download");
        FrameworkElement upload = CreateNetworkRateBlock(
            FormatByteRate(uploadBytes),
            "Upload",
            global::Windows.UI.Color.FromArgb(255, 248, 113, 113));
        FrameworkElement download = CreateNetworkRateBlock(
            FormatByteRate(downloadBytes),
            "Download",
            accent);
        Grid.SetColumn(download, 1);
        rateGrid.Children.Add(upload);
        rateGrid.Children.Add(download);
        stack.Children.Add(rateGrid);

        stack.Children.Add(CreateSectionCaption("Usage history"));
        double uploadPercent = ParsePercentDetail(data.Details, "Upload Percent");
        double downloadPercent = ParsePercentDetail(data.Details, "Download Percent");
        stack.Children.Add(CreateNetworkSparkline(data.HistoryKey, uploadPercent, downloadPercent, columnWidth - 18));

        stack.Children.Add(CreateNetworkSummaryRow(uploadBytes, downloadBytes));
        stack.Children.Add(CreateSectionCaption("Details"));

        stack.Children.Add(CreateDetailRow(new MetricDetail("Public IP", GetDetailValue(data.Details, "Public IP"))));
        stack.Children.Add(CreateDetailRow(new MetricDetail("Local IP", GetDetailValue(data.Details, "Local IP"))));
        stack.Children.Add(CreateDetailRow(new MetricDetail("Interface", GetDetailValue(data.Details, "Interface"))));
        stack.Children.Add(CreateDetailRow(new MetricDetail("Network", GetDetailValue(data.Details, "Network"))));
        stack.Children.Add(CreateDetailRow(new MetricDetail("Physical address", GetDetailValue(data.Details, "Physical address"))));

        shell.Child = stack;
        return shell;
    }

    private FrameworkElement CreateNetworkRateBlock(string value, string label, global::Windows.UI.Color color)
    {
        var stack = new StackPanel { Spacing = 1 };
        stack.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 13,
            FontFamily = (FontFamily)Application.Current.Resources["SfDisplaySemibold"],
            Foreground = CreateValueBrush(245, 42),
            TextWrapping = TextWrapping.NoWrap
        });

        var labelRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3
        };
        labelRow.Children.Add(new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center
        });
        labelRow.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 9,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = CreateSubValueBrush(92, 112),
            TextWrapping = TextWrapping.NoWrap
        });
        stack.Children.Add(labelRow);
        return stack;
    }

    private FrameworkElement CreateNetworkProcessRow(string name, string value)
        => CreateCpuProcessRow(name, value);

    private FrameworkElement CreateNetworkSummaryRow(double uploadBytes, double downloadBytes)
    {
        double totalBytes = uploadBytes + downloadBytes;
        string activity = totalBytes switch
        {
            >= 5 * 1024 * 1024 => "Heavy activity",
            >= 512 * 1024 => "Active",
            >= 32 * 1024 => "Light traffic",
            _ => "Idle"
        };

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };

        grid.Children.Add(new TextBlock
        {
            Text = "Transfer state",
            FontSize = 9,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = CreateSubValueBrush(90, 112)
        });

        var value = new TextBlock
        {
            Text = activity,
            FontSize = 9,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            Foreground = CreateValueBrush(224, 50),
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right
        };
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);

        return new Border
        {
            Padding = new Thickness(6, 4, 6, 4),
            CornerRadius = new CornerRadius(6),
            Background = UseLightTheme
                ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(74, 255, 255, 255))
                : new SolidColorBrush(global::Windows.UI.Color.FromArgb(46, 255, 255, 255)),
            Child = grid
        };
    }

    private FrameworkElement CreateNetworkSparkline(string key, double uploadPercent, double downloadPercent, double width)
    {
        Queue<(double Up, double Down)> history = TrackNetworkHistory(key, uploadPercent, downloadPercent);
        double graphWidth = Math.Max(72, width);
        double graphHeight = 38;
        double centerY = graphHeight / 2;

        var canvas = new Canvas
        {
            Width = graphWidth,
            Height = graphHeight,
            Background = CreateInsetBrush(global::Windows.UI.Color.FromArgb(255, 127, 200, 216), darkAlpha: 22, lightAlpha: 18)
        };

        canvas.Children.Add(new Line
        {
            X1 = 0,
            X2 = graphWidth,
            Y1 = centerY,
            Y2 = centerY,
            Stroke = new SolidColorBrush(global::Windows.UI.Color.FromArgb(60, 148, 163, 184)),
            StrokeThickness = 1
        });

        var uploadLine = new Polyline
        {
            Stroke = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 248, 113, 113)),
            StrokeThickness = 1.3,
            Opacity = 0.9
        };
        var downloadLine = new Polyline
        {
            Stroke = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 34, 211, 238)),
            StrokeThickness = 1.3,
            Opacity = 0.9
        };

        (double Up, double Down)[] values = history.ToArray();
        if (values.Length == 1)
        {
            values = [values[0], values[0]];
        }

        for (int i = 0; i < values.Length; i++)
        {
            double x = values.Length <= 1 ? 0 : i / (double)(values.Length - 1) * graphWidth;
            double uploadY = centerY - (Math.Clamp(values[i].Up, 0, 100) / 100.0 * centerY);
            double downloadY = centerY + (Math.Clamp(values[i].Down, 0, 100) / 100.0 * centerY);
            uploadLine.Points.Add(new global::Windows.Foundation.Point(x, uploadY));
            downloadLine.Points.Add(new global::Windows.Foundation.Point(x, downloadY));
        }

        canvas.Children.Add(uploadLine);
        canvas.Children.Add(downloadLine);

        return new Border
        {
            Height = graphHeight,
            CornerRadius = new CornerRadius(6),
            Child = canvas,
            Clip = new RectangleGeometry { Rect = new global::Windows.Foundation.Rect(0, 0, graphWidth, graphHeight) }
        };
    }

    private static UIElement CreateProgressRingArc(double percent, global::Windows.UI.Color accent)
        => CreateProgressRingArc(percent, accent, 56, 4);

    private static UIElement CreateProgressRingArc(double percent, global::Windows.UI.Color accent, double size, double strokeThickness)
    {
        double normalized = Math.Clamp(percent / 100.0, 0, 1);
        if (normalized <= 0.001)
        {
            return new Grid { Width = size, Height = size };
        }

        double radius = (size - strokeThickness) / 2;
        double center = size / 2;
        double angle = -90 + (Math.Min(normalized, 0.9999) * 360);
        double radians = angle * Math.PI / 180;
        var start = new global::Windows.Foundation.Point(center, center - radius);
        var end = new global::Windows.Foundation.Point(
            center + (radius * Math.Cos(radians)),
            center + (radius * Math.Sin(radians)));

        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new global::Windows.Foundation.Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = normalized > 0.5
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Width = size,
            Height = size,
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = strokeThickness,
            Data = geometry
        };
    }

    private static IReadOnlyList<UIElement> CreateProgressRingSegments(IReadOnlyList<UsageSegment> segments, double size, double strokeThickness)
    {
        var paths = new List<UIElement>();
        double offset = 0;

        foreach (UsageSegment segment in segments)
        {
            double normalized = Math.Clamp(segment.Percent / 100.0, 0, 1);
            if (normalized <= 0.001)
            {
                continue;
            }

            paths.Add(CreateProgressRingSegment(offset, normalized, segment.Color, size, strokeThickness));
            offset += normalized;
            if (offset >= 0.999)
            {
                break;
            }
        }

        return paths;
    }

    private static UIElement CreateProgressRingSegment(
        double startOffset,
        double length,
        global::Windows.UI.Color color,
        double size,
        double strokeThickness)
    {
        double clampedStart = Math.Clamp(startOffset, 0, 0.9999);
        double clampedEnd = Math.Clamp(startOffset + length, 0, 0.9999);
        double radius = (size - strokeThickness) / 2;
        double center = size / 2;
        double startAngle = -90 + (clampedStart * 360);
        double endAngle = -90 + (clampedEnd * 360);
        double startRadians = startAngle * Math.PI / 180;
        double endRadians = endAngle * Math.PI / 180;

        var start = new global::Windows.Foundation.Point(
            center + (radius * Math.Cos(startRadians)),
            center + (radius * Math.Sin(startRadians)));
        var end = new global::Windows.Foundation.Point(
            center + (radius * Math.Cos(endRadians)),
            center + (radius * Math.Sin(endRadians)));

        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = false
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new global::Windows.Foundation.Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = length > 0.5
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Width = size,
            Height = size,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = strokeThickness,
            Data = geometry
        };
    }

    private FrameworkElement CreateSparkline(string key, double value, global::Windows.UI.Color accent, double width)
    {
        Queue<double> history = TrackHistory(key, value);
        double graphWidth = Math.Max(72, width);
        double graphHeight = 28;
        var canvas = new Canvas
        {
            Width = graphWidth,
            Height = graphHeight,
            Background = CreateInsetBrush(accent)
        };

        var line = new Polyline
        {
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 1.4,
            Opacity = 0.88
        };

        double[] values = history.ToArray();
        if (values.Length == 1)
        {
            values = [values[0], values[0]];
        }

        for (int i = 0; i < values.Length; i++)
        {
            double x = values.Length <= 1 ? 0 : i / (double)(values.Length - 1) * graphWidth;
            double y = graphHeight - (Math.Clamp(values[i], 0, 100) / 100.0 * graphHeight);
            line.Points.Add(new global::Windows.Foundation.Point(x, y));
        }

        canvas.Children.Add(line);
        return new Border
        {
            Height = graphHeight,
            CornerRadius = new CornerRadius(6),
            Child = canvas,
            Clip = new RectangleGeometry { Rect = new global::Windows.Foundation.Rect(0, 0, graphWidth, graphHeight) }
        };
    }

    private Queue<double> TrackHistory(string key, double value)
    {
        if (!_historyByKey.TryGetValue(key, out Queue<double>? history))
        {
            history = new Queue<double>();
            _historyByKey[key] = history;
        }

        history.Enqueue(Math.Clamp(value, 0, 100));
        while (history.Count > 24)
        {
            history.Dequeue();
        }

        return history;
    }

    private Queue<(double Up, double Down)> TrackNetworkHistory(string key, double uploadPercent, double downloadPercent)
    {
        if (!_networkHistoryByKey.TryGetValue(key, out Queue<(double Up, double Down)>? history))
        {
            history = new Queue<(double Up, double Down)>();
            _networkHistoryByKey[key] = history;
        }

        history.Enqueue((Math.Clamp(uploadPercent, 0, 100), Math.Clamp(downloadPercent, 0, 100)));
        while (history.Count > 24)
        {
            history.Dequeue();
        }

        return history;
    }

    private FrameworkElement CreateDetailRow(MetricDetail detail)
    {
        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            ColumnSpacing = 6,
            Margin = new Thickness(0, 1, 0, 1)
        };

        row.Children.Add(new TextBlock
        {
            Text = detail.Label,
            FontSize = 9,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = CreateSubValueBrush(86, 116),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var value = new TextBlock
        {
            Text = detail.Value,
            FontSize = 9,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            Foreground = CreateValueBrush(220, 42),
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }

    private void AddProfileSummary()
    {
        var store = WindowsProfileStore.Current;
        WindowsProfile? activeProfile = store.Profiles.FirstOrDefault(p => p.Id == store.ActiveProfileId);

        string name;
        string summary;
        if (activeProfile is not null)
        {
            name = activeProfile.Name;
            summary = activeProfile.BuildSummary();
        }
        else if (store.BaseProfile is not null)
        {
            name = "Previous State";
            summary = store.BaseProfile.BuildSummary();
        }
        else
        {
            name = "Windows";
            summary = "No Veil profile active";
        }

        var header = new Grid();

        var copy = new StackPanel { Spacing = 1 };
        copy.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 13,
            FontFamily = (FontFamily)Application.Current.Resources["SfDisplaySemibold"],
            Foreground = CreateValueBrush(245, 42),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        copy.Children.Add(new TextBlock
        {
            Text = summary,
            FontSize = 9,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = CreateSubValueBrush(120, 118),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        header.Children.Add(copy);

        StatsPanel.Children.Add(header);
    }

    private void AddTitle(string title, double topMargin)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.Resources["SfDisplaySemibold"],
            Foreground = CreateLabelBrush(150, 130),
            Margin = new Thickness(0, topMargin, 0, 1)
        };
        StatsPanel.Children.Add(titleText);
    }

    private void AddStatSection(string label, string valueStr, double percent, string detail)
    {
        StatsPanel.Children.Add(CreateStatSection(label, valueStr, percent, detail, Math.Max(0, _panelViewWidth - HorizontalContentInset)));
    }

    private FrameworkElement CreateStatSection(string label, string valueStr, double percent, string detail, double barMaxWidth)
    {
        var headerRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Margin = new Thickness(0, 0, 0, 3)
        };

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            Foreground = CreateLabelBrush(120, 138)
        };

        var valueText = new TextBlock
        {
            Text = valueStr,
            FontSize = 10,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            Foreground = CreateValueBrush(220, 34),
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right
        };

        Grid.SetColumn(labelText, 0);
        Grid.SetColumn(valueText, 2);
        headerRow.Children.Add(labelText);
        headerRow.Children.Add(valueText);

        var barBg = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(2),
            Background = CreateBarBackgroundBrush(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var barColor = percent switch
        {
            > 85 => UseLightTheme
                ? global::Windows.UI.Color.FromArgb(214, 0, 0, 0)
                : global::Windows.UI.Color.FromArgb(200, 255, 255, 255),
            > 65 => UseLightTheme
                ? global::Windows.UI.Color.FromArgb(210, 0, 0, 0)
                : global::Windows.UI.Color.FromArgb(180, 255, 255, 255),
            _ => UseLightTheme
                ? global::Windows.UI.Color.FromArgb(180, 0, 0, 0)
                : global::Windows.UI.Color.FromArgb(150, 255, 255, 255)
        };

        var barFill = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(barColor),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = Math.Max(0, barMaxWidth * Math.Clamp(percent / 100.0, 0, 1))
        };

        var barGrid = new Grid { Children = { barBg, barFill } };

        var detailText = new TextBlock
        {
            Text = detail,
            FontSize = 9,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = CreateSubValueBrush(80, 118),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var section = new StackPanel { Spacing = 0 };
        section.Children.Add(headerRow);
        section.Children.Add(barGrid);
        section.Children.Add(detailText);

        return section;
    }

    private void AddInfoSection(string label, string valueStr, string detail)
    {
        var headerRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Margin = new Thickness(0, 0, 0, 2)
        };

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            Foreground = CreateLabelBrush(120, 138)
        };

        var valueText = new TextBlock
        {
            Text = valueStr,
            FontSize = 10,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            Foreground = CreateValueBrush(220, 34),
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = Math.Max(72, _panelViewWidth - 112)
        };

        Grid.SetColumn(labelText, 0);
        Grid.SetColumn(valueText, 2);
        headerRow.Children.Add(labelText);
        headerRow.Children.Add(valueText);

        var detailText = new TextBlock
        {
            Text = detail,
            FontSize = 9,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = CreateSubValueBrush(80, 118),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var section = new StackPanel { Spacing = 2 };
        section.Children.Add(headerRow);
        section.Children.Add(detailText);

        StatsPanel.Children.Add(section);
    }

    private double GetCpuUsage()
    {
        try
        {
            if (_cpuCounter is not null)
            {
                return Math.Clamp(_cpuCounter.NextValue(), 0, 100);
            }
        }
        catch { }
        return 0;
    }

    private double GetCpuUserUsage()
    {
        try
        {
            if (_cpuUserCounter is not null)
            {
                return Math.Clamp(_cpuUserCounter.NextValue(), 0, 100);
            }
        }
        catch { }
        return 0;
    }

    private double GetCpuSystemUsage()
    {
        try
        {
            if (_cpuSystemCounter is not null)
            {
                return Math.Clamp(_cpuSystemCounter.NextValue(), 0, 100);
            }
        }
        catch { }
        return 0;
    }

    private void StartProcessRefreshAsync()
    {
        if (Interlocked.Exchange(ref _processRefreshInFlight, 1) != 0)
        {
            return;
        }

        DateTime prevSampleUtc = _lastProcessCpuSampleUtc;
        Dictionary<int, TimeSpan> prevTimes = _lastProcessCpuTimes;

        _ = Task.Run(() =>
        {
            try
            {
                var (cpuList, newTimes, newSampleUtc) = ComputeTopCpuProcesses(prevSampleUtc, prevTimes);
                var memList = ComputeTopMemoryProcesses();

                DispatcherQueue.TryEnqueue(() =>
                {
                    _lastProcessCpuSampleUtc = newSampleUtc;
                    _lastProcessCpuTimes = newTimes;
                    _cachedTopCpuProcesses = cpuList;
                    _cachedTopMemProcesses = memList;
                });
            }
            finally
            {
                Interlocked.Exchange(ref _processRefreshInFlight, 0);
            }
        });
    }

    private static (IReadOnlyList<CpuProcessUsage> List, Dictionary<int, TimeSpan> NewTimes, DateTime SampleUtc) ComputeTopCpuProcesses(
        DateTime prevSampleUtc, Dictionary<int, TimeSpan> prevTimes)
    {
        DateTime nowUtc = DateTime.UtcNow;
        var currentTimes = new Dictionary<int, TimeSpan>();
        var samples = new List<CpuProcessUsage>();

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                using (process)
                {
                    currentTimes[process.Id] = process.TotalProcessorTime;
                    if (prevSampleUtc == default
                        || !prevTimes.TryGetValue(process.Id, out TimeSpan previousTime))
                    {
                        continue;
                    }

                    double elapsedMs = (nowUtc - prevSampleUtc).TotalMilliseconds;
                    double cpuMs = (process.TotalProcessorTime - previousTime).TotalMilliseconds;
                    if (elapsedMs <= 0 || cpuMs <= 0 || Environment.ProcessorCount <= 0)
                    {
                        continue;
                    }

                    double percent = Math.Clamp(cpuMs / (elapsedMs * Environment.ProcessorCount) * 100.0, 0, 100);
                    if (percent < 0.1)
                    {
                        continue;
                    }

                    samples.Add(new CpuProcessUsage(GetProcessDisplayName(process), percent));
                }
            }
            catch
            {
                process.Dispose();
            }
        }

        IReadOnlyList<CpuProcessUsage> result = samples
            .OrderByDescending(static sample => sample.Percent)
            .ThenBy(static sample => sample.Name, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();

        return (result, currentTimes, nowUtc);
    }

    private static IReadOnlyList<MemoryProcessUsage> ComputeTopMemoryProcesses()
    {
        var samples = new List<MemoryProcessUsage>();

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                using (process)
                {
                    process.Refresh();
                    long workingSetBytes = process.WorkingSet64;
                    if (workingSetBytes <= 0)
                    {
                        continue;
                    }

                    samples.Add(new MemoryProcessUsage(GetProcessDisplayName(process), workingSetBytes));
                }
            }
            catch
            {
                process.Dispose();
            }
        }

        return samples
            .OrderByDescending(static sample => sample.WorkingSetBytes)
            .ThenBy(static sample => sample.Name, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
    }

    private static string GetProcessDisplayName(Process process)
    {
        try
        {
            string? fileDescription = process.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(fileDescription))
            {
                return fileDescription.Trim();
            }
        }
        catch
        {
        }

        return string.IsNullOrWhiteSpace(process.ProcessName)
            ? $"PID {process.Id}"
            : process.ProcessName;
    }

    private NetworkStats GetNetworkStats()
    {
        long receivedBytes = 0;
        long sentBytes = 0;
        string interfaceName = "Offline";
        string localIp = "Unavailable";
        string networkName = "Unavailable";
        string physicalAddress = "Unavailable";

        try
        {
            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPv4InterfaceStatistics stats = networkInterface.GetIPv4Statistics();
                receivedBytes += stats.BytesReceived;
                sentBytes += stats.BytesSent;

                if (interfaceName == "Offline")
                {
                    interfaceName = networkInterface.Name;
                    localIp = GetLocalIpAddress(networkInterface);
                    networkName = networkInterface.Description;
                    physicalAddress = FormatPhysicalAddress(networkInterface.GetPhysicalAddress());
                }
            }
        }
        catch
        {
            return new NetworkStats("0 KB/s", 0, 0, 0, "Unavailable", "Unavailable", "Unavailable", "Unavailable");
        }

        DateTime nowUtc = DateTime.UtcNow;
        if (_lastNetworkSampleUtc == default)
        {
            _lastNetworkSampleUtc = nowUtc;
            _lastNetworkReceivedBytes = receivedBytes;
            _lastNetworkSentBytes = sentBytes;
            return new NetworkStats("0 KB/s", 0, 0, 0, interfaceName, localIp, networkName, physicalAddress);
        }

        double elapsedSeconds = Math.Max(0.001, (nowUtc - _lastNetworkSampleUtc).TotalSeconds);
        double receivedPerSecond = Math.Max(0, receivedBytes - _lastNetworkReceivedBytes) / elapsedSeconds;
        double sentPerSecond = Math.Max(0, sentBytes - _lastNetworkSentBytes) / elapsedSeconds;

        _lastNetworkSampleUtc = nowUtc;
        _lastNetworkReceivedBytes = receivedBytes;
        _lastNetworkSentBytes = sentBytes;

        double totalPerSecond = receivedPerSecond + sentPerSecond;
        double percent = GetNetworkActivityPercent(totalPerSecond);
        return new NetworkStats(FormatByteRate(totalPerSecond), percent, receivedPerSecond, sentPerSecond, interfaceName, localIp, networkName, physicalAddress);
    }

    private static string GetLocalIpAddress(NetworkInterface networkInterface)
    {
        try
        {
            return networkInterface.GetIPProperties()
                .UnicastAddresses
                .Where(static address => address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(static address => address.Address.ToString())
                .FirstOrDefault() ?? "Unavailable";
        }
        catch
        {
            return "Unavailable";
        }
    }

    private static string FormatPhysicalAddress(PhysicalAddress physicalAddress)
    {
        byte[] bytes = physicalAddress.GetAddressBytes();
        return bytes.Length == 0
            ? "Unavailable"
            : string.Join(":", bytes.Select(static value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private bool TryGetVeilStats(MemoryStatusEx memoryStatus, out VeilStats stats)
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();

            DateTime nowUtc = DateTime.UtcNow;
            TimeSpan totalProcessorTime = process.TotalProcessorTime;
            double cpuPercent = 0;

            if (_lastVeilCpuSampleUtc != default)
            {
                double elapsedMs = (nowUtc - _lastVeilCpuSampleUtc).TotalMilliseconds;
                double cpuMs = (totalProcessorTime - _lastVeilCpuTime).TotalMilliseconds;
                if (elapsedMs > 0 && Environment.ProcessorCount > 0)
                {
                    cpuPercent = Math.Clamp(cpuMs / (elapsedMs * Environment.ProcessorCount) * 100.0, 0, 100);
                }
            }

            _lastVeilCpuSampleUtc = nowUtc;
            _lastVeilCpuTime = totalProcessorTime;

            double memoryPercent = 0;
            if (memoryStatus.ullTotalPhys > 0)
            {
                memoryPercent = Math.Clamp(process.WorkingSet64 / (double)memoryStatus.ullTotalPhys * 100.0, 0, 100);
            }

            stats = new VeilStats(
                cpuPercent,
                memoryPercent,
                process.WorkingSet64,
                process.PrivateMemorySize64,
                process.Threads.Count,
                process.HandleCount,
                process.PriorityClass);
            return true;
        }
        catch
        {
            stats = default;
            return false;
        }
    }

    private static string ShortenCpuName(string name)
    {
        return name
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("CPU", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Processor", "", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ")
            .Replace("  ", " ")
            .Replace(" @", ",")
            .Trim();
    }

    private static string ShortenGpuName(string name)
    {
        return name
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ")
            .Replace("  ", " ")
            .Trim();
    }

    private static string FormatBytes(long bytes)
    {
        double megabytes = bytes / (1024.0 * 1024);
        if (megabytes >= 1024)
        {
            return $"{megabytes / 1024:F2} GB";
        }

        return $"{megabytes:F0} MB";
    }

    private static string FormatGpuMemory(ulong usedBytes, ulong totalBytes)
    {
        double usedMb = usedBytes / (1024.0 * 1024);
        double totalMb = totalBytes / (1024.0 * 1024);
        if (totalMb >= 1024)
        {
            return $"{usedMb / 1024:F1} / {totalMb / 1024:F1} GB";
        }

        return $"{usedMb:F0} / {totalMb:F0} MB";
    }

    private static string FormatTemperature(double celsius)
    {
        return $"{celsius:F0} \u00B0C";
    }

    private static string GetPressureLabel(double percent)
    {
        return percent switch
        {
            >= 85 => "High",
            >= 65 => "Moderate",
            _ => "Low"
        };
    }

    private static double ParsePercentDetail(IEnumerable<MetricDetail> details, string label)
    {
        string? value = details
            .FirstOrDefault(detail => string.Equals(detail.Label, label, StringComparison.OrdinalIgnoreCase))
            .Value;

        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        value = value.Trim().TrimEnd('%');
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double percent)
            ? Math.Clamp(percent, 0, 100)
            : 0;
    }

    private static string GetDetailValue(IEnumerable<MetricDetail> details, string label)
    {
        string value = details
            .FirstOrDefault(detail => string.Equals(detail.Label, label, StringComparison.OrdinalIgnoreCase))
            .Value;

        return string.IsNullOrWhiteSpace(value) ? "Unavailable" : value;
    }

    private static string FormatByteRate(double bytesPerSecond)
    {
        double kilobytes = bytesPerSecond / 1024.0;
        if (kilobytes >= 1024)
        {
            return $"{kilobytes / 1024:F1} MB/s";
        }

        return $"{kilobytes:F0} KB/s";
    }

    private static double GetNetworkActivityPercent(double bytesPerSecond)
        => Math.Clamp(bytesPerSecond / (12.5 * 1024 * 1024) * 100, 0, 100);

    private static double ParseByteRateDetail(IEnumerable<MetricDetail> details, string label)
    {
        string rawLabel = $"{label} Raw";
        string value = details
            .FirstOrDefault(detail => string.Equals(detail.Label, rawLabel, StringComparison.OrdinalIgnoreCase))
            .Value;

        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double bytesPerSecond)
            ? Math.Max(0, bytesPerSecond)
            : 0;
    }

    private static IReadOnlyList<UsageSegment> CreateCpuUsageSegments(IReadOnlyList<CpuProcessUsage> processes)
    {
        return processes
            .Take(5)
            .Select(process => new UsageSegment(process.Percent, GetProcessColor(process.Name)))
            .Where(static segment => segment.Percent > 0)
            .ToArray();
    }

    private static IReadOnlyList<UsageSegment> CreateMemoryUsageSegments(IReadOnlyList<MemoryProcessUsage> processes, double totalBytes)
    {
        if (totalBytes <= 0)
        {
            return [];
        }

        return processes
            .Take(5)
            .Select(process => new UsageSegment(Math.Clamp(process.WorkingSetBytes / totalBytes * 100.0, 0, 100), GetProcessColor(process.Name)))
            .Where(static segment => segment.Percent > 0)
            .ToArray();
    }

    private static global::Windows.UI.Color GetProcessColor(string name)
    {
        int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(name);
        int index = Math.Abs(hash == int.MinValue ? 0 : hash) % ProcessPalette.Length;
        return ParseHexColor(ProcessPalette[index]);
    }

    private static double ParseFormattedByteValue(string value)
    {
        string[] parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return 0;
        }

        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double amount))
        {
            return 0;
        }

        return parts[1].ToUpperInvariant() switch
        {
            "GB" => amount * 1024 * 1024 * 1024,
            "MB" => amount * 1024 * 1024,
            "KB" => amount * 1024,
            _ => amount
        };
    }

    private static string FormatPriorityClass(ProcessPriorityClass priorityClass)
    {
        return priorityClass switch
        {
            ProcessPriorityClass.Idle => "Idle",
            ProcessPriorityClass.BelowNormal => "Below normal",
            ProcessPriorityClass.Normal => "Normal",
            ProcessPriorityClass.AboveNormal => "Above normal",
            ProcessPriorityClass.High => "High",
            ProcessPriorityClass.RealTime => "Real-time",
            _ => priorityClass.ToString()
        };
    }

    private static global::Windows.UI.Color ParseHexColor(string value)
    {
        string hex = value.Trim().TrimStart('#');
        if (hex.Length != 6)
        {
            return global::Windows.UI.Color.FromArgb(255, 255, 255, 255);
        }

        return global::Windows.UI.Color.FromArgb(
            255,
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    internal void RefreshAppearance()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        SetupAcrylic();
        int panelWidth = IsStatsVisible && GetWindowRect(_hwnd, out var rect)
            ? GetTargetPanelWidth(rect.Right, rect.Top)
            : GetTargetPanelWidth(0, 0);
        _panelViewWidth = Math.Max(1, WindowHelper.PhysicalPixelsToView(this, panelWidth));
        BuildAndRefresh();

        if (!IsStatsVisible)
        {
            return;
        }

        int panelHeight = CalculateHeight();
        var appWindow = WindowHelper.GetAppWindow(this);
        var pos = appWindow.Position;
        var bounds = ResolveWindowBounds(pos.X, pos.Y, panelWidth, panelHeight);
        ApplyPanelSize(bounds.Width, bounds.Height);
        appWindow.MoveAndResize(bounds);
        WindowHelper.ApplyRoundedRegion(_hwnd, bounds.Width, bounds.Height, PanelCornerRadius);
    }

    private SolidColorBrush CreateLabelBrush(byte darkAlpha, byte lightAlpha)
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(lightAlpha, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb((byte)Math.Max((int)darkAlpha, 214), 255, 255, 255));
    }

    private SolidColorBrush CreateValueBrush(byte darkAlpha, byte lightAlpha)
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(lightAlpha, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb((byte)Math.Max((int)darkAlpha, 248), 255, 255, 255));
    }

    private SolidColorBrush CreateSubValueBrush(byte darkAlpha, byte lightAlpha)
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(lightAlpha, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb((byte)Math.Max((int)darkAlpha, 184), 255, 255, 255));
    }

    private SolidColorBrush CreateBarBackgroundBrush()
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(32, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(18, 255, 255, 255));
    }

    private global::Windows.Graphics.RectInt32 ResolveWindowBounds(int x, int y, int width, int height)
    {
        var workArea = ResolveWorkArea(x, y);
        int boundedWidth = Math.Min(width, Math.Max(1, workArea.Right - workArea.Left - (ScreenMargin * 2)));
        int boundedHeight = Math.Min(height, Math.Max(1, workArea.Bottom - workArea.Top - (ScreenMargin * 2)));
        int boundedX = Math.Clamp(x, workArea.Left + ScreenMargin, workArea.Right - boundedWidth - ScreenMargin);
        int boundedY = Math.Clamp(y, workArea.Top + ScreenMargin, workArea.Bottom - boundedHeight - ScreenMargin);

        return new global::Windows.Graphics.RectInt32(boundedX, boundedY, boundedWidth, boundedHeight);
    }

    private Rect ResolveWorkArea(int x, int y)
    {
        foreach (MonitorInfo2 monitor in MonitorService.GetAllMonitors())
        {
            Rect area = monitor.WorkArea;
            if (x >= area.Left && x < area.Right && y >= area.Top && y < area.Bottom)
            {
                return area;
            }
        }

        return MonitorService.GetAllMonitors()
            .OrderByDescending(static monitor => monitor.IsPrimary)
            .Select(static monitor => monitor.WorkArea)
            .FirstOrDefault();
    }

    private int GetTargetPanelWidth(int x, int y)
    {
        int gpuCount = Math.Max(_lastGpuCount, GetCurrentGpuCount());
        int gpuOverflow = Math.Max(0, gpuCount - 1);
        int targetViewWidth = Math.Min(MaximumPanelWidth, PanelWidth + (gpuOverflow * MultiGpuPanelWidthStep));
        int desiredWidth = Math.Max(1, WindowHelper.ViewPixelsToPhysical(this, targetViewWidth));
        var workArea = ResolveWorkArea(x, y);
        return Math.Min(desiredWidth, Math.Max(1, workArea.Right - workArea.Left - (ScreenMargin * 2)));
    }

    private int GetCurrentGpuCount()
    {
        try
        {
            return GraphicsMemoryMonitor.GetAllGpuInfo().Count;
        }
        catch
        {
            return _lastGpuCount;
        }
    }

    private void ApplyPanelSize(int physicalWidth, int physicalHeight)
    {
        double viewWidth = Math.Max(1, WindowHelper.PhysicalPixelsToView(this, physicalWidth));
        double viewHeight = Math.Max(1, WindowHelper.PhysicalPixelsToView(this, physicalHeight));
        PanelBorder.Width = viewWidth;
        PanelBorder.MaxWidth = viewWidth;
        PanelBorder.Height = viewHeight;
    }

    private readonly record struct VeilStats(
        double CpuPercent,
        double MemoryPercent,
        long WorkingSetBytes,
        long PrivateBytes,
        int ThreadCount,
        int HandleCount,
        ProcessPriorityClass PriorityClass);

    private readonly record struct CpuProcessUsage(string Name, double Percent);

    private readonly record struct MemoryProcessUsage(string Name, long WorkingSetBytes);

    private readonly record struct UsageSegment(double Percent, global::Windows.UI.Color Color);

    private readonly record struct MetricColumnData(
        string Title,
        string Value,
        double Percent,
        string AccentHex,
        string HistoryKey,
        IReadOnlyList<MetricDetail> Details);

    private readonly record struct MetricDetail(string Label, string Value);

    private readonly record struct NetworkStats(
        string Value,
        double Percent,
        double ReceivedBytesPerSecond,
        double SentBytesPerSecond,
        string InterfaceName,
        string LocalIp,
        string NetworkName,
        string PhysicalAddress);
}
