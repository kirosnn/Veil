using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Veil.Diagnostics;
using Veil.Interop;
using Veil.Services;
using WinRT;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

public sealed partial class TopBarWindow
{
    private void OnVisualTick(object? sender, object e)
    {
        using var perfScope = PerformanceLogger.Measure("TopBar.OnVisualTick", 2.0);
        if (_isGameMinimalMode || _isHidden)
        {
            return;
        }

        if (_settings.TopBarStyle == "Adaptive")
        {
            UpdateAdaptiveBackground(GetEffectiveTopBarOpacity());
        }
    }

    private void UpdateVisualRefreshState(bool boost = false)
    {
        if (boost)
        {
            _lastAdaptiveVisualBoostUtc = DateTime.UtcNow;
        }

        _adaptiveVisualModule.Update(EvaluateAdaptiveVisualDemand());
    }

    private ModuleDemand EvaluateAdaptiveVisualDemand()
    {
        if (_isGameMinimalMode || _isHidden || _settings.TopBarStyle != "Adaptive")
        {
            return ModuleDemand.Cold("adaptive-disabled");
        }

        if (DateTime.UtcNow - _lastAdaptiveVisualBoostUtc <= AdaptiveVisualHotHoldDuration)
        {
            return ModuleDemand.Hot("adaptive-boost");
        }

        return ModuleDemand.Warm("adaptive-visible");
    }

    private void OnAdaptiveVisualTemperatureChanged(ModuleTemperature previousTemperature, ModuleTemperature nextTemperature, string reason)
    {
        if (nextTemperature == ModuleTemperature.Cold)
        {
            _visualTimer.Stop();
            return;
        }

        _visualTimer.Interval = nextTemperature == ModuleTemperature.Hot
            ? AdaptiveVisualRefreshInterval
            : AdaptiveVisualWarmRefreshInterval;
        _visualTimer.Start();

        if (previousTemperature == ModuleTemperature.Cold || nextTemperature == ModuleTemperature.Hot)
        {
            UpdateAdaptiveBackground(GetEffectiveTopBarOpacity(), true);
        }
    }

    private void ResetAdaptiveModeState()
    {
        _hasAdaptiveModeState = false;
        _pendingAdaptiveClearMode = null;
        _pendingAdaptiveClearModeUtc = DateTime.MinValue;
    }

    private void EnsureTopBarAcrylic()
    {
        double intensity = GetEffectiveBlurIntensity();
        bool needsRecreate = _acrylicController == null
            || Math.Abs(_lastAppliedBlurIntensity - intensity) > 0.005;

        if (!needsRecreate)
        {
            return;
        }

        _lastAppliedBlurIntensity = intensity;

        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfig = null;

        float tintOpacity = (float)(intensity * 0.18);
        float luminosity = (float)Math.Min(intensity * 0.85, 0.90);
        byte tintAlpha = (byte)(intensity * 50);

        _acrylicController = new DesktopAcrylicController
        {
            TintColor = global::Windows.UI.Color.FromArgb(tintAlpha, 245, 245, 250),
            TintOpacity = tintOpacity,
            LuminosityOpacity = luminosity,
            FallbackColor = global::Windows.UI.Color.FromArgb(20, 240, 240, 245)
        };

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark
        };

        _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
    }

    private void RemoveTopBarAcrylic()
    {
        _lastAppliedBlurIntensity = -1;
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfig = null;

        if (_hwnd != IntPtr.Zero)
        {
            var blurBehind = new DwmBlurBehind
            {
                dwFlags = DWM_BB_ENABLE,
                fEnable = false,
                hRgnBlur = IntPtr.Zero
            };
            DwmEnableBlurBehindWindow(_hwnd, ref blurBehind);
            try
            {
                var target = this.As<ICompositionSupportsSystemBackdrop>();
                target.SystemBackdrop = null;
            }
            catch { }
        }
    }

    private void EnsureTransparentBackdrop()
    {
        _lastAppliedBlurIntensity = -1;
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfig = null;

        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var region = CreateRectRgn(-2, -2, -1, -1);
        var blurBehind = new DwmBlurBehind
        {
            dwFlags = DWM_BB_ENABLE | DWM_BB_BLURREGION,
            fEnable = true,
            hRgnBlur = region
        };
        DwmEnableBlurBehindWindow(_hwnd, ref blurBehind);
        if (region != IntPtr.Zero)
        {
            DeleteObject(region);
        }

        var compositor = new global::Windows.UI.Composition.Compositor();
        var transparentBrush = compositor.CreateColorBrush(
            global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
        var target = this.As<ICompositionSupportsSystemBackdrop>();
        target.SystemBackdrop = transparentBrush;
    }

    private SolidColorBrush CreateBlurBackgroundBrush()
    {
        return new SolidColorBrush(global::Windows.UI.Color.FromArgb(
            (byte)Math.Round(GetEffectiveTopBarOpacity() * 28), 255, 255, 255));
    }

    private void ApplyForegroundTheme()
    {
        double buttonOpacity = _settings.TopBarStyle == "Solid" ? 1 : 0.92;
        var foregroundColor = GetTopBarForegroundColor();
        var foregroundBrush = new SolidColorBrush(foregroundColor);
        var mutedForegroundBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(204, foregroundColor.R, foregroundColor.G, foregroundColor.B));
        var hoverBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(12, foregroundColor.R, foregroundColor.G, foregroundColor.B));
        var pressedBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(8, foregroundColor.R, foregroundColor.G, foregroundColor.B));

        ClockText.Foreground = foregroundBrush;
        WeatherButton.Foreground = foregroundBrush;
        WeatherButtonText.Foreground = foregroundBrush;
        MusicIcon.Foreground = mutedForegroundBrush;
        FinderButton.Foreground = foregroundBrush;
        FinderButtonText.Foreground = foregroundBrush;
        DiscordIcon.Source = UseDarkTopBarForeground(foregroundColor)
            ? DiscordDarkIconSource
            : DiscordLightIconSource;
        PanelThemeIcon.Source = UseDarkTopBarForeground(foregroundColor)
            ? PanelThemeDarkIconSource
            : PanelThemeLightIconSource;

        MenuButton.Opacity = buttonOpacity;
        FinderButton.Opacity = buttonOpacity;
        WeatherButton.Opacity = buttonOpacity;
        DiscordButton.Opacity = buttonOpacity;
        MusicButton.Opacity = buttonOpacity;
        RunCatButton.Opacity = buttonOpacity;
        PanelThemeButton.Opacity = buttonOpacity;

        MenuButton.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(170, foregroundColor.R, foregroundColor.G, foregroundColor.B));
        MenuButton.Resources["ButtonBackgroundPointerOver"] = foregroundBrush;
        MenuButton.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(119, foregroundColor.R, foregroundColor.G, foregroundColor.B));

        ApplyIconButtonResources(WeatherButton, hoverBrush, pressedBrush);
        ApplyIconButtonResources(DiscordButton, hoverBrush, pressedBrush);
        ApplyIconButtonResources(MusicButton, hoverBrush, pressedBrush);
        ApplyIconButtonResources(RunCatButton, hoverBrush, pressedBrush);
        ApplyIconButtonResources(PanelThemeButton, hoverBrush, pressedBrush);

        byte bubbleAlpha = _settings.ShowFinderBubble
            ? (byte)Math.Round(_settings.FinderBubbleOpacity * 255)
            : (byte)0;
        FinderButton.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(
            bubbleAlpha, foregroundColor.R, foregroundColor.G, foregroundColor.B));
        FinderButton.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(48, foregroundColor.R, foregroundColor.G, foregroundColor.B));
        FinderButton.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(28, foregroundColor.R, foregroundColor.G, foregroundColor.B));

        FinderButton.CornerRadius = new CornerRadius(11);
        foreach (Button shortcutButton in ShortcutButtonsPanel.Children.OfType<Button>())
        {
            var fc = foregroundColor;
            shortcutButton.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(
                _settings.ShowFinderBubble ? (byte)Math.Round(_settings.FinderBubbleOpacity * 215) : (byte)0,
                fc.R, fc.G, fc.B));
            shortcutButton.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(230, fc.R, fc.G, fc.B));
            shortcutButton.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(48, fc.R, fc.G, fc.B));
            shortcutButton.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(global::Windows.UI.Color.FromArgb(28, fc.R, fc.G, fc.B));
            if (shortcutButton.Content is TextBlock tb)
            {
                tb.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(230, fc.R, fc.G, fc.B));
            }
            shortcutButton.CornerRadius = new CornerRadius(9);
        }

        UpdateGlassPanels(true);
        UpdateWeatherButton();
        _ = ApplyRunCatSettingsAsync();
    }

    private void UpdateGlassPanels(bool visible)
    {
        if (visible && ShortcutButtonsPanel.Children.Count > 0)
        {
            var foregroundColor = GetTopBarForegroundColor();
            ShortcutGlass.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(
                18, foregroundColor.R, foregroundColor.G, foregroundColor.B));
            ShortcutGlass.BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(
                188, foregroundColor.R, foregroundColor.G, foregroundColor.B));
            ShortcutGlass.BorderThickness = new Thickness(1.2);
            ShortcutGlass.Padding = new Thickness(3, 2, 3, 2);
        }
        else
        {
            ShortcutGlass.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ShortcutGlass.BorderBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ShortcutGlass.BorderThickness = new Thickness(0);
            ShortcutGlass.Padding = new Thickness(0);
        }
    }

    private void UpdateShortcutGlassBlurRegion()
    {
        _lastShortcutGlassBlurRegionSignature = string.Empty;
    }

    private global::Windows.UI.Color GetTopBarForegroundColor(byte alpha = 255)
    {
        if (_adaptiveForegroundOverride.HasValue)
        {
            var ov = _adaptiveForegroundOverride.Value;
            return global::Windows.UI.Color.FromArgb(alpha, ov.R, ov.G, ov.B);
        }
        var (r, g, b) = ParseHexColor(_settings.TopBarForegroundColor);
        return global::Windows.UI.Color.FromArgb(alpha, r, g, b);
    }

    private static bool UseDarkTopBarForeground(global::Windows.UI.Color color)
    {
        double luminance = (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);
        return luminance < 140;
    }

    private static void ApplyIconButtonResources(Button button, SolidColorBrush hoverBrush, SolidColorBrush pressedBrush)
    {
        button.Resources["ButtonBackgroundPointerOver"] = hoverBrush;
        button.Resources["ButtonBackgroundPressed"] = pressedBrush;
    }

    private void UpdateAdaptiveBackground(double effectiveTopBarOpacity, bool force = false)
    {
        bool hasTopContentWindow = WindowHelper.TryGetTopContentWindowForScreen(_screen, _hwnd, out IntPtr topWindowOnScreen, out Rect topWindowRect);
        bool wantsClear = !hasTopContentWindow
            || !(IsZoomed(topWindowOnScreen) || IsFullscreenLike(topWindowRect));

        wantsClear = ResolveAdaptiveClearMode(wantsClear);

        if (wantsClear)
        {
            UpdateAdaptiveClearMode(force);
        }
        else
        {
            UpdateAdaptiveOpaqueMode(effectiveTopBarOpacity, force);
        }
    }

    private bool ResolveAdaptiveClearMode(bool wantsClear)
    {
        if (!_hasAdaptiveModeState)
        {
            _hasAdaptiveModeState = true;
            _pendingAdaptiveClearMode = null;
            _pendingAdaptiveClearModeUtc = DateTime.MinValue;
            return wantsClear;
        }

        if (wantsClear == _isAdaptiveClearModeActive)
        {
            _pendingAdaptiveClearMode = null;
            _pendingAdaptiveClearModeUtc = DateTime.MinValue;
            return wantsClear;
        }

        DateTime nowUtc = DateTime.UtcNow;
        if (_pendingAdaptiveClearMode != wantsClear)
        {
            _pendingAdaptiveClearMode = wantsClear;
            _pendingAdaptiveClearModeUtc = nowUtc;
            return _isAdaptiveClearModeActive;
        }

        TimeSpan transitionDelay = wantsClear
            ? AdaptiveClearTransitionDelay
            : AdaptiveOpaqueTransitionDelay;
        if (nowUtc - _pendingAdaptiveClearModeUtc < transitionDelay)
        {
            return _isAdaptiveClearModeActive;
        }

        _pendingAdaptiveClearMode = null;
        _pendingAdaptiveClearModeUtc = DateTime.MinValue;
        return wantsClear;
    }

    private void UpdateAdaptiveClearMode(bool force)
    {
        if (!_isAdaptiveClearModeActive)
        {
            _isAdaptiveClearModeActive = true;
            _lastAdaptiveColor = null;
            _lastAdaptiveProbeColor = null;
            _lastAdaptiveDeepSampleUtc = DateTime.MinValue;
            _lastAdaptiveClearSampleUtc = DateTime.MinValue;

            EnsureTransparentBackdrop();
            WindowHelper.DisableLayeredTransparency(this);
            RootPanel.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
            UpdateGlassPanels(true);
            force = true;
        }

        DateTime nowUtc = DateTime.UtcNow;
        bool shouldRunDeepSample = force || nowUtc - _lastAdaptiveClearSampleUtc >= AdaptiveVisualDeepSampleInterval;

        if (!TrySampleAdaptiveProbeColor(out global::Windows.UI.Color probeColor))
        {
            probeColor = _lastAdaptiveProbeColor ?? global::Windows.UI.Color.FromArgb(255, 20, 20, 22);
            shouldRunDeepSample = true;
        }

        bool probeChanged = !AreColorsClose(probeColor, _lastAdaptiveProbeColor);
        _lastAdaptiveProbeColor = probeColor;

        if (!force && !probeChanged && !shouldRunDeepSample)
        {
            return;
        }

        var wallpaperColor = SampleScreenColorBelowTopBar();
        _lastAdaptiveClearSampleUtc = nowUtc;

        if (!force && AreColorsClose(wallpaperColor, _lastAdaptiveColor))
        {
            return;
        }

        _lastAdaptiveColor = wallpaperColor;
        _lastAdaptiveProbeColor = wallpaperColor;

        double clearLuminance = (0.2126 * wallpaperColor.R) + (0.7152 * wallpaperColor.G) + (0.0722 * wallpaperColor.B);
        var newForeground = clearLuminance > 186
            ? global::Windows.UI.Color.FromArgb(255, 0, 0, 0)
            : global::Windows.UI.Color.FromArgb(255, 255, 255, 255);

        if (_adaptiveForegroundOverride == null
            || _adaptiveForegroundOverride.Value.R != newForeground.R
            || _adaptiveForegroundOverride.Value.G != newForeground.G
            || _adaptiveForegroundOverride.Value.B != newForeground.B)
        {
            _adaptiveForegroundOverride = newForeground;
            ApplyForegroundTheme();
        }
    }

    private void UpdateAdaptiveOpaqueMode(double effectiveTopBarOpacity, bool force)
    {
        if (_isAdaptiveClearModeActive)
        {
            _isAdaptiveClearModeActive = false;
            RemoveTopBarAcrylic();
            WindowHelper.DisableLayeredTransparency(this);
            _lastAdaptiveColor = null;
            _lastAdaptiveProbeColor = null;
            _lastAdaptiveDeepSampleUtc = DateTime.MinValue;
            UpdateGlassPanels(true);
            force = true;
        }

        using var perfScope = PerformanceLogger.Measure("TopBar.UpdateAdaptiveBackground", 1.5);
        DateTime nowUtc = DateTime.UtcNow;
        bool shouldRunDeepSample = force || nowUtc - _lastAdaptiveDeepSampleUtc >= AdaptiveVisualDeepSampleInterval;

        if (!TrySampleAdaptiveProbeColor(out global::Windows.UI.Color probeColor))
        {
            probeColor = _lastAdaptiveProbeColor ?? global::Windows.UI.Color.FromArgb(255, 20, 20, 22);
            shouldRunDeepSample = true;
        }

        bool probeChanged = !AreColorsClose(probeColor, _lastAdaptiveProbeColor);
        _lastAdaptiveProbeColor = probeColor;

        if (!force && !probeChanged && !shouldRunDeepSample)
        {
            return;
        }

        var adaptiveColor = SampleScreenColorBelowTopBar();
        _lastAdaptiveDeepSampleUtc = nowUtc;

        if (!force && AreColorsClose(adaptiveColor, _lastAdaptiveColor))
        {
            return;
        }

        _lastAdaptiveColor = adaptiveColor;

        if (_acrylicController != null)
        {
            _lastAppliedBlurIntensity = -1;
            _acrylicController.Dispose();
            _acrylicController = null;
            _backdropConfig = null;
        }

        RootPanel.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(
            255, adaptiveColor.R, adaptiveColor.G, adaptiveColor.B));

        double bgLuminance = (0.2126 * adaptiveColor.R) + (0.7152 * adaptiveColor.G) + (0.0722 * adaptiveColor.B);
        var newForeground = bgLuminance > 186
            ? global::Windows.UI.Color.FromArgb(255, 0, 0, 0)
            : global::Windows.UI.Color.FromArgb(255, 255, 255, 255);

        if (_adaptiveForegroundOverride == null
            || _adaptiveForegroundOverride.Value.R != newForeground.R
            || _adaptiveForegroundOverride.Value.G != newForeground.G
            || _adaptiveForegroundOverride.Value.B != newForeground.B)
        {
            _adaptiveForegroundOverride = newForeground;
            ApplyForegroundTheme();
        }
    }

    private static bool AreColorsClose(global::Windows.UI.Color? left, global::Windows.UI.Color? right)
    {
        if (!left.HasValue || !right.HasValue)
        {
            return left.HasValue == right.HasValue;
        }

        return Math.Abs(left.Value.R - right.Value.R) <= 5
            && Math.Abs(left.Value.G - right.Value.G) <= 5
            && Math.Abs(left.Value.B - right.Value.B) <= 5;
    }

    private global::Windows.UI.Color SampleScreenColorBelowTopBar()
    {
        using var perfScope = PerformanceLogger.Measure("TopBar.SampleScreenColorBelowTopBar", 1.0);
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return global::Windows.UI.Color.FromArgb(255, 20, 20, 22);
        }

        try
        {
            int screenWidth = _screen.Right - _screen.Left;
            int sampleY = _screen.Top + BarHeight;

            Span<int> xOffsets = stackalloc int[]
            {
                _screen.Left + (int)(screenWidth * 0.10),
                _screen.Left + screenWidth / 2,
                _screen.Left + (int)(screenWidth * 0.90)
            };

            const int maxSamples = 3;
            Span<uint> samples = stackalloc uint[maxSamples];
            int sampleCount = 0;

            foreach (int x in xOffsets)
            {
                uint colorValue = GetPixel(screenDc, x, sampleY);
                if (colorValue != 0xFFFFFFFF)
                {
                    samples[sampleCount++] = colorValue;
                }
            }

            if (sampleCount == 0)
            {
                return global::Windows.UI.Color.FromArgb(255, 20, 20, 22);
            }

            uint bestColor = samples[0];
            int bestVotes = 0;

            for (int i = 0; i < sampleCount; i++)
            {
                int votes = 0;
                for (int j = 0; j < sampleCount; j++)
                {
                    if (ColorsWithinTolerance(samples[i], samples[j], 15))
                    {
                        votes++;
                    }
                }
                if (votes > bestVotes)
                {
                    bestVotes = votes;
                    bestColor = samples[i];
                }
            }

            byte r = (byte)(bestColor & 0xFF);
            byte g = (byte)((bestColor >> 8) & 0xFF);
            byte b = (byte)((bestColor >> 16) & 0xFF);
            return global::Windows.UI.Color.FromArgb(255, r, g, b);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private bool IsFullscreenLike(Rect windowRect)
    {
        const int tolerance = 2;
        return windowRect.Left <= _screen.Left + tolerance
            && windowRect.Top <= _screen.Top + tolerance
            && windowRect.Right >= _screen.Right - tolerance
            && windowRect.Bottom >= _screen.Bottom - tolerance;
    }

    private bool TrySampleAdaptiveProbeColor(out global::Windows.UI.Color color)
    {
        color = default;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            int screenWidth = _screen.Right - _screen.Left;
            int sampleX = _screen.Left + (screenWidth / 2);
            int sampleY = _screen.Top + BarHeight;
            uint colorValue = GetPixel(screenDc, sampleX, sampleY);
            if (colorValue == 0xFFFFFFFF)
            {
                return false;
            }

            color = ColorFromPixelValue(colorValue);
            return true;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static global::Windows.UI.Color ColorFromPixelValue(uint colorValue)
    {
        byte r = (byte)(colorValue & 0xFF);
        byte g = (byte)((colorValue >> 8) & 0xFF);
        byte b = (byte)((colorValue >> 16) & 0xFF);
        return global::Windows.UI.Color.FromArgb(255, r, g, b);
    }

    private static bool ColorsWithinTolerance(uint a, uint b, int tolerance)
    {
        int dr = (int)(a & 0xFF) - (int)(b & 0xFF);
        int dg = (int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF);
        int db = (int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF);
        return Math.Abs(dr) <= tolerance && Math.Abs(dg) <= tolerance && Math.Abs(db) <= tolerance;
    }

    private static string FormatColorHex(global::Windows.UI.Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static (byte R, byte G, byte B) ParseHexColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length < 7)
        {
            return (0, 0, 0);
        }

        try
        {
            byte r = Convert.ToByte(hex[1..3], 16);
            byte g = Convert.ToByte(hex[3..5], 16);
            byte b = Convert.ToByte(hex[5..7], 16);
            return (r, g, b);
        }
        catch
        {
            return (0, 0, 0);
        }
    }
}
