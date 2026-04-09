using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using UnicodeAnimations.Models;
using Veil.Interop;
using WinRT;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class DictationOverlayWindow : Window
{
    private readonly record struct OverlayLayoutMetrics(int Width, int ContentWidth);

    private enum OverlayCenterMode
    {
        Spectrum,
        StatusText,
        TranscribingWave
    }

    private enum OverlayLeftVisual
    {
        None,
        ListeningIcon,
        InsertedIcon,
        CopiedIcon,
        ErrorIcon
    }

    private const int OverlayHeight = 40;
    private const int OverlayCornerRadius = 20;
    private const int OverlayContentWidthPadding = 14;
    private const int OverlayContentChromeWidth = 72;
    private const int OverlayHorizontalMargin = 12;
    private const int OverlayMinimumWidth = 132;
    private const int OverlayStatusChromeWidth = 80;
    private const int OverlayTranscribingWaveSpacingWidth = 8;
    private const double StatusTextDefaultFontSize = 12.5;
    private const double TranscribingWaveFontSize = 13;
    private const int ListeningOverlayWidth = 184;
    private const int TranscribingOverlayWidth = 216;
    private const int InsertedOverlayWidth = 160;
    private const int CopiedOverlayWidth = 160;
    private const int ErrorOverlayWidth = 160;
    private static readonly Spinner TranscribingWaveAnimation = SpinnerRegistry.All.First(static entry => entry.Name == "waverows").Spinner;
    private readonly DispatcherTimer _spectrumTimer;
    private readonly DispatcherTimer _transcribingWaveTimer;
    private readonly Border[] _spectrumBars;
    private readonly Random _random = new();
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;
    private IntPtr _hwnd;
    private int _animationStep;
    private int _transcribingWaveFrameIndex;
    private bool _useLiveSpectrum;
    private string _dismissHoverHex = "#26FFFFFF";
    private OverlayCenterMode _centerMode = OverlayCenterMode.Spectrum;
    private int _overlayWidth = ListeningOverlayWidth;

    internal event Action? DismissRequested;

    public DictationOverlayWindow()
    {
        InitializeComponent();
        _spectrumBars =
        [
            SpectrumBar01,
            SpectrumBar02,
            SpectrumBar03,
            SpectrumBar04,
            SpectrumBar05,
            SpectrumBar06,
            SpectrumBar07,
            SpectrumBar08,
            SpectrumBar09
        ];
        _spectrumTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(90)
        };
        _spectrumTimer.Tick += OnSpectrumTick;
        _transcribingWaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(TranscribingWaveAnimation.Interval)
        };
        _transcribingWaveTimer.Tick += OnTranscribingWaveTick;
        TranscribingWaveTextBlock.Text = TranscribingWaveAnimation.Frames[0];
        Activated += OnActivated;
        Closed += OnClosed;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnActivated;

        _hwnd = WindowHelper.GetHwnd(this);
        WindowHelper.RemoveTitleBar(this);
        WindowHelper.MakeOverlay(this);
        WindowHelper.PrepareForSystemBackdrop(this);
        WindowHelper.SetAlwaysOnTop(this);
        WindowHelper.ApplyRoundedRegion(this, _overlayWidth, OverlayHeight, OverlayCornerRadius);
        SetupAcrylic(
            tintColor: global::Windows.UI.Color.FromArgb(255, 28, 32, 40),
            tintOpacity: 0.2f,
            luminosityOpacity: 0.52f,
            fallbackColor: global::Windows.UI.Color.FromArgb(216, 19, 22, 27));
        ShowWindowNative(_hwnd, SW_HIDE);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _spectrumTimer.Stop();
        _transcribingWaveTimer.Stop();
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfig = null;
        _hwnd = IntPtr.Zero;
    }

    internal void ShowStatus(ScreenBounds screen, string title, string detail)
    {
        StatusTextBlock.Text = GetCompactStatusText(title, detail);
        TranscribingLabelTextBlock.Text = StatusTextBlock.Text;

        var monitor = MonitorService.GetAllMonitors()
            .FirstOrDefault(candidate => candidate.Bounds.Left == screen.Left &&
                                         candidate.Bounds.Top == screen.Top &&
                                         candidate.Bounds.Right == screen.Right &&
                                         candidate.Bounds.Bottom == screen.Bottom);

        int left = monitor?.WorkArea.Left ?? screen.Left;
        int right = monitor?.WorkArea.Right ?? screen.Right;
        int bottom = monitor?.WorkArea.Bottom ?? screen.Bottom;
        int workAreaWidth = Math.Max(1, right - left);
        int horizontalMargin = workAreaWidth > OverlayHorizontalMargin * 2 ? OverlayHorizontalMargin : 0;
        int availableWidth = Math.Max(1, workAreaWidth - (horizontalMargin * 2));

        ApplyVisualState(title, availableWidth);

        if (_hwnd == IntPtr.Zero)
        {
            Activate();
        }

        WindowHelper.ApplyRoundedRegion(this, _overlayWidth, OverlayHeight, OverlayCornerRadius);

        int x = left + Math.Max(0, (workAreaWidth - _overlayWidth) / 2);
        int y = bottom - OverlayHeight - 14;

        WindowHelper.PositionOnMonitor(this, x, y, _overlayWidth, OverlayHeight);
        ShowWindowNative(_hwnd, SW_SHOWNOACTIVATE);
    }

    internal void HideOverlay()
    {
        _spectrumTimer.Stop();
        StopTranscribingWaveAnimation();
        SetDismissButtonVisual(isHovered: false, isPressed: false);
        if (_hwnd != IntPtr.Zero)
        {
            ShowWindowNative(_hwnd, SW_HIDE);
        }
    }

    private void OnDismissButtonClick(object sender, TappedRoutedEventArgs e)
    {
        DismissRequested?.Invoke();
    }

    private void OnDismissButtonPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetDismissButtonVisual(isHovered: true, isPressed: false);
    }

    private void OnDismissButtonPointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetDismissButtonVisual(isHovered: false, isPressed: false);
    }

    private void OnDismissButtonPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        SetDismissButtonVisual(isHovered: true, isPressed: true);
    }

    private void OnDismissButtonPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        SetDismissButtonVisual(isHovered: true, isPressed: false);
    }

    internal void SetSpectrumLevels(IReadOnlyList<float> levels)
    {
        if (_centerMode != OverlayCenterMode.Spectrum)
        {
            return;
        }

        _useLiveSpectrum = true;
        _spectrumTimer.Stop();
        SetCenterVisualState(OverlayCenterMode.Spectrum);

        for (int index = 0; index < _spectrumBars.Length; index++)
        {
            float value = index < levels.Count ? levels[index] : 0;
            value = Math.Clamp(value, 0f, 1f);
            double targetHeight = 4 + (Math.Pow(value, 0.7) * 16);
            double heightLerp = value > 0.08f ? 0.58 : 0.22;
            _spectrumBars[index].Height = Lerp(_spectrumBars[index].Height, Math.Clamp(targetHeight, 4, 20), heightLerp);
            _spectrumBars[index].Opacity = Math.Max(0.2, value * 1.7);
        }
    }

    private void OnSpectrumTick(object? sender, object e)
    {
        if (_useLiveSpectrum)
        {
            return;
        }

        _animationStep++;
        for (int index = 0; index < _spectrumBars.Length; index++)
        {
            double wave = Math.Abs(Math.Sin((_animationStep + (index * 1.35)) * 0.42));
            double jitter = _random.NextDouble() * 2.5;
            double height = 4 + (wave * 12.5) + jitter;
            _spectrumBars[index].Height = Math.Clamp(height, 4, 20);
            _spectrumBars[index].Opacity = 0.45 + (wave * 0.4);
        }
    }

    private void OnTranscribingWaveTick(object? sender, object e)
    {
        if (_centerMode != OverlayCenterMode.TranscribingWave)
        {
            return;
        }

        _transcribingWaveFrameIndex = (_transcribingWaveFrameIndex + 1) % TranscribingWaveAnimation.Frames.Length;
        TranscribingWaveTextBlock.Text = TranscribingWaveAnimation.Frames[_transcribingWaveFrameIndex];
    }

    private void ApplyVisualState(string title, int availableWidth)
    {
        string backgroundHex = "#9013181D";
        string foregroundHex = "#FFF4FBFF";
        string buttonForegroundHex = "#DDF4FBFF";
        string accentHex = "#FFF4FBFF";
        global::Windows.UI.Color acrylicTintColor = global::Windows.UI.Color.FromArgb(255, 28, 32, 40);
        global::Windows.UI.Color acrylicFallbackColor = global::Windows.UI.Color.FromArgb(216, 19, 22, 27);
        float acrylicTintOpacity = 0.2f;
        float acrylicLuminosityOpacity = 0.52f;
        OverlayLeftVisual leftVisual = OverlayLeftVisual.ListeningIcon;
        OverlayCenterMode centerMode = OverlayCenterMode.Spectrum;
        bool showDismiss = true;

        if (title.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            _useLiveSpectrum = false;
            backgroundHex = "#90171518";
            foregroundHex = "#FFFFE1E1";
            buttonForegroundHex = "#FFFFE1E1";
            accentHex = "#FFFFB4B4";
            acrylicTintColor = global::Windows.UI.Color.FromArgb(255, 52, 24, 29);
            acrylicFallbackColor = global::Windows.UI.Color.FromArgb(214, 34, 19, 22);
            acrylicTintOpacity = 0.24f;
            acrylicLuminosityOpacity = 0.48f;
            leftVisual = OverlayLeftVisual.ErrorIcon;
            centerMode = OverlayCenterMode.StatusText;
            showDismiss = false;
        }
        else if (title.Equals("Inserted", StringComparison.OrdinalIgnoreCase))
        {
            _useLiveSpectrum = false;
            foregroundHex = "#FFE9FFF4";
            buttonForegroundHex = "#FFE9FFF4";
            accentHex = "#FFA8FFD6";
            acrylicTintColor = global::Windows.UI.Color.FromArgb(255, 24, 37, 34);
            acrylicFallbackColor = global::Windows.UI.Color.FromArgb(210, 18, 28, 26);
            acrylicTintOpacity = 0.22f;
            acrylicLuminosityOpacity = 0.5f;
            leftVisual = OverlayLeftVisual.InsertedIcon;
            centerMode = OverlayCenterMode.StatusText;
            showDismiss = false;
        }
        else if (title.Equals("Copied", StringComparison.OrdinalIgnoreCase))
        {
            _useLiveSpectrum = false;
            foregroundHex = "#FFEBF4FF";
            buttonForegroundHex = "#FFEBF4FF";
            accentHex = "#FFB8D9FF";
            acrylicTintColor = global::Windows.UI.Color.FromArgb(255, 27, 32, 41);
            acrylicFallbackColor = global::Windows.UI.Color.FromArgb(210, 20, 23, 29);
            acrylicTintOpacity = 0.22f;
            acrylicLuminosityOpacity = 0.5f;
            leftVisual = OverlayLeftVisual.CopiedIcon;
            centerMode = OverlayCenterMode.StatusText;
            showDismiss = false;
        }
        else if (title.Equals("Transcribing", StringComparison.OrdinalIgnoreCase))
        {
            _useLiveSpectrum = false;
            foregroundHex = "#FFE8F6FF";
            buttonForegroundHex = "#FFE8F6FF";
            accentHex = "#FFA3D4FF";
            acrylicTintColor = global::Windows.UI.Color.FromArgb(255, 28, 34, 44);
            acrylicFallbackColor = global::Windows.UI.Color.FromArgb(212, 20, 24, 31);
            acrylicTintOpacity = 0.22f;
            acrylicLuminosityOpacity = 0.52f;
            leftVisual = OverlayLeftVisual.None;
            centerMode = OverlayCenterMode.TranscribingWave;
            showDismiss = false;
        }
        else
        {
            _useLiveSpectrum = true;
            ApplySilentShape();
            _spectrumTimer.Start();
        }

        ApplyLayoutMetrics(title, availableWidth);
        SetupAcrylic(acrylicTintColor, acrylicTintOpacity, acrylicLuminosityOpacity, acrylicFallbackColor);

        PanelBorder.Background = CreateBrush(backgroundHex);
        PanelBorder.BorderBrush = CreateBrush("#00000000");
        StatusTextBlock.Foreground = CreateBrush(foregroundHex);
        TranscribingLabelTextBlock.Foreground = CreateBrush(foregroundHex);
        TranscribingWaveTextBlock.Foreground = CreateBrush(accentHex);
        DismissIconPath.Stroke = CreateBrush(buttonForegroundHex);
        ListeningIconBodyPath.Stroke = CreateBrush(foregroundHex);
        ListeningIconStemPath.Stroke = CreateBrush(foregroundHex);
        InsertedIconPath.Stroke = CreateBrush(accentHex);
        CopiedIconBackPath.Stroke = CreateBrush(foregroundHex);
        CopiedIconFrontPath.Stroke = CreateBrush(foregroundHex);
        ErrorIconOutline.Stroke = CreateBrush(accentHex);
        ErrorIconLinePath.Stroke = CreateBrush(accentHex);
        ErrorIconDot.Fill = CreateBrush(accentHex);

        foreach (Border bar in _spectrumBars)
        {
            bar.Background = CreateBrush(accentHex);
        }

        _dismissHoverHex = title.Contains("Error", StringComparison.OrdinalIgnoreCase)
            ? "#22FF8F8F"
            : title.Equals("Inserted", StringComparison.OrdinalIgnoreCase)
                ? "#22A8FFD6"
                : title.Equals("Copied", StringComparison.OrdinalIgnoreCase)
                    ? "#2295BDFF"
                    : title.Equals("Transcribing", StringComparison.OrdinalIgnoreCase)
                        ? "#227CB8FF"
                        : "#20F1FAFF";

        SetLeftVisualState(leftVisual);
        SetCenterVisualState(centerMode);
        DismissButtonContainer.Visibility = showDismiss ? Visibility.Visible : Visibility.Collapsed;
        SetDismissButtonVisual(isHovered: false, isPressed: false);

        if (!_useLiveSpectrum)
        {
            _spectrumTimer.Stop();
        }
    }

    private void ApplyLayoutMetrics(string title, int availableWidth)
    {
        OverlayLayoutMetrics metrics = GetLayoutMetrics(title, availableWidth);
        _overlayWidth = metrics.Width;
        ContentGrid.Width = metrics.ContentWidth;
        StatusTextBlock.FontSize = StatusTextDefaultFontSize;
        TranscribingLabelTextBlock.FontSize = StatusTextDefaultFontSize;
        TranscribingWaveTextBlock.FontSize = TranscribingWaveFontSize;
        StatusTextBlock.MaxWidth = Math.Max(0, metrics.ContentWidth - OverlayContentChromeWidth);
    }

    private OverlayLayoutMetrics GetLayoutMetrics(string title, int availableWidth)
    {
        int preferredWidth = ListeningOverlayWidth;

        if (title.Equals("Inserted", StringComparison.OrdinalIgnoreCase))
        {
            preferredWidth = InsertedOverlayWidth;
        }
        else if (title.Equals("Copied", StringComparison.OrdinalIgnoreCase))
        {
            preferredWidth = CopiedOverlayWidth;
        }
        else if (title.Equals("Transcribing", StringComparison.OrdinalIgnoreCase))
        {
            preferredWidth = TranscribingOverlayWidth;
        }
        else if (title.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            preferredWidth = ErrorOverlayWidth;
        }

        int requiredWidth = preferredWidth;
        if (title.Equals("Transcribing", StringComparison.OrdinalIgnoreCase))
        {
            double labelWidth = MeasureTextWidth(TranscribingLabelTextBlock, StatusTextDefaultFontSize);
            double waveWidth = MeasureTranscribingWaveWidth();
            requiredWidth = Math.Max(
                preferredWidth,
                (int)Math.Ceiling(labelWidth + waveWidth + OverlayTranscribingWaveSpacingWidth + OverlayStatusChromeWidth));
        }
        else if (!title.Equals("Listening", StringComparison.OrdinalIgnoreCase))
        {
            double textWidth = MeasureTextWidth(StatusTextBlock, StatusTextDefaultFontSize);
            requiredWidth = Math.Max(preferredWidth, (int)Math.Ceiling(textWidth + OverlayStatusChromeWidth));
        }

        int safeAvailableWidth = Math.Max(1, availableWidth);
        int minimumWidth = Math.Min(OverlayMinimumWidth, safeAvailableWidth);
        int clampedWidth = Math.Clamp(requiredWidth, minimumWidth, safeAvailableWidth);
        int contentWidth = Math.Max(1, requiredWidth - OverlayContentWidthPadding);
        return new OverlayLayoutMetrics(clampedWidth, contentWidth);
    }

    private void ApplySilentShape()
    {
        double[] heights = [5, 7, 10, 8, 15, 10, 10, 7, 5];
        for (int index = 0; index < _spectrumBars.Length; index++)
        {
            _spectrumBars[index].Height = heights[index];
            _spectrumBars[index].Opacity = index is 4 ? 0.72 : 0.38;
        }
    }

    private void SetLeftVisualState(OverlayLeftVisual visual)
    {
        ListeningIcon.Visibility = visual == OverlayLeftVisual.ListeningIcon ? Visibility.Visible : Visibility.Collapsed;
        InsertedIcon.Visibility = visual == OverlayLeftVisual.InsertedIcon ? Visibility.Visible : Visibility.Collapsed;
        CopiedIcon.Visibility = visual == OverlayLeftVisual.CopiedIcon ? Visibility.Visible : Visibility.Collapsed;
        ErrorIcon.Visibility = visual == OverlayLeftVisual.ErrorIcon ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetCenterVisualState(OverlayCenterMode mode)
    {
        _centerMode = mode;
        SpectrumPanel.Visibility = mode == OverlayCenterMode.Spectrum ? Visibility.Visible : Visibility.Collapsed;
        StatusTextBlock.Visibility = mode == OverlayCenterMode.StatusText ? Visibility.Visible : Visibility.Collapsed;
        TranscribingWavePanel.Visibility = mode == OverlayCenterMode.TranscribingWave ? Visibility.Visible : Visibility.Collapsed;

        if (mode == OverlayCenterMode.TranscribingWave)
        {
            StartTranscribingWaveAnimation();
        }
        else
        {
            StopTranscribingWaveAnimation();
        }
    }

    private void SetDismissButtonVisual(bool isHovered, bool isPressed)
    {
        DismissButtonContainer.Background = isHovered
            ? CreateBrush(_dismissHoverHex)
            : CreateBrush("#00000000");

        double scale = isPressed
            ? 0.95
            : isHovered
                ? 1.05
                : 1;

        DismissButtonScaleTransform.ScaleX = scale;
        DismissButtonScaleTransform.ScaleY = scale;
    }

    private void SetupAcrylic(
        global::Windows.UI.Color tintColor,
        float tintOpacity,
        float luminosityOpacity,
        global::Windows.UI.Color fallbackColor)
    {
        _acrylicController?.Dispose();

        _acrylicController = new DesktopAcrylicController
        {
            TintColor = tintColor,
            TintOpacity = tintOpacity,
            LuminosityOpacity = luminosityOpacity,
            FallbackColor = fallbackColor
        };

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = false,
            Theme = SystemBackdropTheme.Dark
        };

        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        return new SolidColorBrush(ColorFromHex(hex));
    }

    private static global::Windows.UI.Color ColorFromHex(string hex)
    {
        string normalized = hex.TrimStart('#');
        if (normalized.Length != 8)
        {
            return global::Windows.UI.Color.FromArgb(255, 255, 255, 255);
        }

        byte a = Convert.ToByte(normalized[..2], 16);
        byte r = Convert.ToByte(normalized[2..4], 16);
        byte g = Convert.ToByte(normalized[4..6], 16);
        byte b = Convert.ToByte(normalized[6..8], 16);
        return global::Windows.UI.Color.FromArgb(a, r, g, b);
    }

    private static string GetCompactStatusText(string title, string detail)
    {
        return title switch
        {
            "Listening" => "Listening",
            "Transcribing" => "Transcribing",
            "Inserted" => "Inserted",
            "Copied" => "Copied",
            _ when title.Contains("Error", StringComparison.OrdinalIgnoreCase) => "Error",
            _ => TrimForCapsule(title)
        };
    }

    private static string TrimForCapsule(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Dictation";
        }

        string trimmed = value.Trim();
        return trimmed.Length > 26 ? trimmed[..26] + "..." : trimmed;
    }

    private static double Lerp(double from, double to, double amount)
    {
        return from + ((to - from) * amount);
    }

    private void StartTranscribingWaveAnimation()
    {
        _transcribingWaveFrameIndex = 0;
        TranscribingWaveTextBlock.Text = TranscribingWaveAnimation.Frames[_transcribingWaveFrameIndex];
        _transcribingWaveTimer.Start();
    }

    private void StopTranscribingWaveAnimation()
    {
        _transcribingWaveTimer.Stop();
        _transcribingWaveFrameIndex = 0;
        TranscribingWaveTextBlock.Text = TranscribingWaveAnimation.Frames[_transcribingWaveFrameIndex];
    }

    private double MeasureTextWidth(TextBlock textBlock, double fontSize)
    {
        double previousFontSize = textBlock.FontSize;
        double previousMaxWidth = textBlock.MaxWidth;

        try
        {
            textBlock.FontSize = fontSize;
            textBlock.MaxWidth = double.PositiveInfinity;
            textBlock.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, OverlayHeight));
            return Math.Ceiling(textBlock.DesiredSize.Width);
        }
        finally
        {
            textBlock.FontSize = previousFontSize;
            textBlock.MaxWidth = previousMaxWidth;
        }
    }

    private double MeasureTranscribingWaveWidth()
    {
        string previousText = TranscribingWaveTextBlock.Text;
        double maxWidth = 0;

        try
        {
            foreach (string frame in TranscribingWaveAnimation.Frames)
            {
                TranscribingWaveTextBlock.Text = frame;
                maxWidth = Math.Max(maxWidth, MeasureTextWidth(TranscribingWaveTextBlock, TranscribingWaveFontSize));
            }

            return Math.Ceiling(maxWidth);
        }
        finally
        {
            TranscribingWaveTextBlock.Text = previousText;
        }
    }
}
