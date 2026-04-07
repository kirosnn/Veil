using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Media;

namespace Veil.Windows;

internal static class PanelGlassPalette
{
    internal static global::Windows.UI.Color GetAcrylicTintColor(bool useLightTheme)
    {
        return useLightTheme
            ? global::Windows.UI.Color.FromArgb(255, 255, 255, 255)
            : global::Windows.UI.Color.FromArgb(255, 28, 28, 34);
    }

    internal static float GetAcrylicTintOpacity(bool useLightTheme)
    {
        return useLightTheme ? 0.06f : 0.12f;
    }

    internal static float GetAcrylicLuminosityOpacity(bool useLightTheme)
    {
        return useLightTheme ? 0.78f : 0.32f;
    }

    internal static global::Windows.UI.Color GetAcrylicFallbackColor(bool useLightTheme)
    {
        return useLightTheme
            ? global::Windows.UI.Color.FromArgb(216, 255, 255, 255)
            : global::Windows.UI.Color.FromArgb(56, 28, 28, 34);
    }

    internal static SystemBackdropTheme GetBackdropTheme(bool useLightTheme)
    {
        return useLightTheme ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;
    }

    internal static SolidColorBrush CreateShellBrush(bool useLightTheme, byte lightAlpha = 38, byte darkAlpha = 16)
    {
        return useLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(lightAlpha, 255, 255, 255))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(darkAlpha, 255, 255, 255));
    }

    internal static SolidColorBrush CreateCardBrush(bool useLightTheme, byte lightAlpha = 24, byte darkAlpha = 18)
    {
        return useLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(lightAlpha, 0, 0, 0))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(darkAlpha, 255, 255, 255));
    }

    internal static SolidColorBrush CreateFrameBrush(bool useLightTheme, byte lightAlpha = 24, byte darkAlpha = 12)
    {
        return useLightTheme
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(lightAlpha, 255, 255, 255))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(darkAlpha, 255, 255, 255));
    }
}
