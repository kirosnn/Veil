using System.Diagnostics;
using Veil.Configuration;
using Veil.Interop;
using Veil.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class SystemStatsWindow : Window
{
    private const int PanelWidth = 250;
    private const int MinimumPanelHeight = 160;
    private const int PanelCornerRadius = 14;
    private const int ScreenMargin = 8;
    private const int HorizontalContentInset = 28;

    private readonly AppSettings _settings = AppSettings.Current;
    private IntPtr _hwnd;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _visibilityTimer;
    private DateTime _openedAtUtc;
    private PerformanceCounter? _cpuCounter;
    private string? _cpuName;
    private DateTime _lastVeilCpuSampleUtc;
    private TimeSpan _lastVeilCpuTime;
    private double _panelViewWidth = PanelWidth;

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

        Task.Run(InitHardwareInfo);
    }

    private bool UseLightTheme => _settings.TopBarPanelTheme == "Light";

    private void InitHardwareInfo()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
            _cpuCounter.NextValue();
        }
        catch
        {
            _cpuCounter = null;
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

    public void Initialize()
    {
        var appWindow = WindowHelper.GetAppWindow(this);
        appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(-9999, -9999, 1, 1));
        Activate();
    }

    public void ShowAt(int x, int y)
    {
        int panelWidth = GetTargetPanelWidth(x, y + 6);
        _panelViewWidth = Math.Max(1, WindowHelper.PhysicalPixelsToView(this, panelWidth));
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

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _settings.Changed -= OnSettingsChanged;
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
        double desiredHeight = Math.Ceiling(StatsPanel.DesiredSize.Height + 24);
        return Math.Max(1, WindowHelper.ViewPixelsToPhysical(this, Math.Max(MinimumPanelHeight, desiredHeight)));
    }

    private void BuildAndRefresh()
    {
        StatsPanel.Children.Clear();

        AddTitle("System");

        double cpuPercent = GetCpuUsage();
        int coreCount = Environment.ProcessorCount;
        string cpuDetail = _cpuName is not null
            ? $"{ShortenCpuName(_cpuName)}  •  {coreCount} threads"
            : $"{coreCount} threads";
        AddStatSection("CPU", $"{cpuPercent:F0}%", cpuPercent, cpuDetail);

        var memoryStatus = MemoryStatusEx.Create();
        if (GlobalMemoryStatusEx(ref memoryStatus) && memoryStatus.ullTotalPhys > 0)
        {
            double usedBytes = memoryStatus.ullTotalPhys - memoryStatus.ullAvailPhys;
            double totalGb = memoryStatus.ullTotalPhys / (1024.0 * 1024 * 1024);
            double usedGb = usedBytes / (1024.0 * 1024 * 1024);
            double percent = usedBytes / memoryStatus.ullTotalPhys * 100;
            string detail = $"{usedGb:F1} / {totalGb:F1} GB";
            AddStatSection("RAM", $"{percent:F0}%", percent, detail);
        }

        var gpus = GraphicsMemoryMonitor.GetAllGpuInfo();
        foreach (var gpu in gpus)
        {
            string shortName = ShortenGpuName(gpu.Name);

            if (gpu.TotalBytes == 0)
            {
                AddStatSection("GPU", "—", 0, shortName);
                continue;
            }

            double usedMb = gpu.UsedBytes / (1024.0 * 1024);
            double totalMb = gpu.TotalBytes / (1024.0 * 1024);
            string sizeUnit = totalMb >= 1024 ? "GB" : "MB";
            double usedDisplay = sizeUnit == "GB" ? usedMb / 1024 : usedMb;
            double totalDisplay = sizeUnit == "GB" ? totalMb / 1024 : totalMb;

            string memDetail = $"{shortName}  •  {usedDisplay:F1} / {totalDisplay:F1} {sizeUnit}";
            string valueStr = $"{gpu.UsagePercent:F0}%";
            AddStatSection("GPU", valueStr, gpu.UsagePercent, memDetail);
        }

        if (TryGetVeilStats(memoryStatus, out VeilStats veilStats))
        {
            AddTitle("Veil");
            string cpuDetailText = $"{FormatPriorityClass(veilStats.PriorityClass)}  •  {veilStats.ThreadCount} threads  •  {veilStats.HandleCount} handles";
            AddStatSection("CPU", $"{veilStats.CpuPercent:F0}%", veilStats.CpuPercent, cpuDetailText);

            string ramDetailText = $"{FormatBytes(veilStats.WorkingSetBytes)} working set  •  {FormatBytes(veilStats.PrivateBytes)} private";
            AddStatSection("RAM", $"{veilStats.MemoryPercent:F1}%", veilStats.MemoryPercent, ramDetailText);
        }
    }

    private void AddTitle(string title)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.Resources["SfDisplaySemibold"],
            Foreground = CreateLabelBrush(150, 130),
            Margin = new Thickness(0, 0, 0, 4)
        };
        StatsPanel.Children.Add(titleText);
    }

    private void AddStatSection(string label, string valueStr, double percent, string detail)
    {
        var headerRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Margin = new Thickness(0, 0, 0, 4)
        };

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            Foreground = CreateLabelBrush(120, 138)
        };

        var valueText = new TextBlock
        {
            Text = valueStr,
            FontSize = 11,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextMedium"],
            Foreground = CreateValueBrush(220, 34),
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right
        };

        Grid.SetColumn(labelText, 0);
        Grid.SetColumn(valueText, 2);
        headerRow.Children.Add(labelText);
        headerRow.Children.Add(valueText);

        double barMaxWidth = Math.Max(0, _panelViewWidth - HorizontalContentInset);
        var barBg = new Border
        {
            Height = 4,
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
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(barColor),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = Math.Max(0, barMaxWidth * Math.Clamp(percent / 100.0, 0, 1))
        };

        var barGrid = new Grid { Children = { barBg, barFill } };

        var detailText = new TextBlock
        {
            Text = detail,
            FontSize = 10,
            FontFamily = (FontFamily)Application.Current.Resources["SfTextRegular"],
            Foreground = CreateSubValueBrush(80, 118),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0)
        };

        var section = new StackPanel { Spacing = 0 };
        section.Children.Add(headerRow);
        section.Children.Add(barGrid);
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
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(darkAlpha, 255, 255, 255));
    }

    private SolidColorBrush CreateValueBrush(byte darkAlpha, byte lightAlpha)
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(lightAlpha, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(darkAlpha, 255, 255, 255));
    }

    private SolidColorBrush CreateSubValueBrush(byte darkAlpha, byte lightAlpha)
    {
        return UseLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(lightAlpha, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(darkAlpha, 255, 255, 255));
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
        int desiredWidth = Math.Max(1, WindowHelper.ViewPixelsToPhysical(this, PanelWidth));
        var workArea = ResolveWorkArea(x, y);
        return Math.Min(desiredWidth, Math.Max(1, workArea.Right - workArea.Left - (ScreenMargin * 2)));
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
}
