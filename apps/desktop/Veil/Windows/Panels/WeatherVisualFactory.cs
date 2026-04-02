using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Veil.Windows;

internal static class WeatherVisualFactory
{
    private static readonly Dictionary<string, ImageSource> LightIconSources = new(StringComparer.Ordinal)
    {
        ["sun"] = CreateSource("Weather", "sun"),
        ["moon-star"] = CreateSource("Weather", "moon-star"),
        ["cloud"] = CreateSource("Weather", "cloud"),
        ["cloud-rain"] = CreateSource("Weather", "cloud-rain"),
        ["cloud-snow"] = CreateSource("Weather", "cloud-snow"),
        ["cloud-lightning"] = CreateSource("Weather", "cloud-lightning"),
        ["cloud-fog"] = CreateSource("Weather", "cloud-fog"),
        ["cloud-sun"] = CreateSource("Weather", "cloud-sun"),
        ["cloud-moon"] = CreateSource("Weather", "cloud-moon")
    };

    private static readonly Dictionary<string, ImageSource> DarkIconSources = new(StringComparer.Ordinal)
    {
        ["sun"] = CreateSource("WeatherDark", "sun"),
        ["moon-star"] = CreateSource("WeatherDark", "moon-star"),
        ["cloud"] = CreateSource("WeatherDark", "cloud"),
        ["cloud-rain"] = CreateSource("WeatherDark", "cloud-rain"),
        ["cloud-snow"] = CreateSource("WeatherDark", "cloud-snow"),
        ["cloud-lightning"] = CreateSource("WeatherDark", "cloud-lightning"),
        ["cloud-fog"] = CreateSource("WeatherDark", "cloud-fog"),
        ["cloud-sun"] = CreateSource("WeatherDark", "cloud-sun"),
        ["cloud-moon"] = CreateSource("WeatherDark", "cloud-moon")
    };

    internal static FrameworkElement CreateIcon(int weatherCode, bool isDay, double size, bool useLightSurface = true)
    {
        return new Image
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Source = ResolveSource(weatherCode, isDay, useLightSurface)
        };
    }

    private static ImageSource ResolveSource(int weatherCode, bool isDay, bool useLightSurface)
    {
        string key = weatherCode switch
        {
            0 => isDay ? "sun" : "moon-star",
            1 or 2 => isDay ? "cloud-sun" : "cloud-moon",
            3 => "cloud",
            45 or 48 => "cloud-fog",
            51 or 53 or 55 or 56 or 57 => "cloud-rain",
            61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "cloud-rain",
            71 or 73 or 75 or 77 or 85 or 86 => "cloud-snow",
            95 or 96 or 99 => "cloud-lightning",
            _ => "cloud"
        };

        return useLightSurface ? LightIconSources[key] : DarkIconSources[key];
    }

    private static ImageSource CreateSource(string folder, string name)
    {
        return new SvgImageSource(new Uri($"ms-appx:///Assets/Icons/{folder}/{name}.svg"));
    }
}
