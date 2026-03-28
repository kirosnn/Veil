using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Veil.Windows;

internal static class PanelButtonFactory
{
    internal static Button Create(
        object? content,
        Brush background,
        Brush foreground,
        Brush pointerOverBackground,
        Brush pressedBackground,
        RoutedEventHandler? onClick = null,
        double? width = null,
        double? height = null,
        Thickness? padding = null,
        CornerRadius? cornerRadius = null,
        object? tag = null,
        bool isEnabled = true,
        bool isTabStop = false,
        HorizontalAlignment horizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalAlignment horizontalContentAlignment = HorizontalAlignment.Center,
        VerticalAlignment verticalAlignment = VerticalAlignment.Center,
        VerticalAlignment verticalContentAlignment = VerticalAlignment.Center)
    {
        var button = new Button
        {
            Content = content,
            Width = width ?? double.NaN,
            Height = height ?? double.NaN,
            Padding = padding ?? new Thickness(0),
            CornerRadius = cornerRadius ?? new CornerRadius(12),
            BorderThickness = new Thickness(0),
            Background = background,
            Foreground = foreground,
            Tag = tag,
            IsEnabled = isEnabled,
            IsTabStop = isTabStop,
            UseSystemFocusVisuals = false,
            HorizontalAlignment = horizontalAlignment,
            HorizontalContentAlignment = horizontalContentAlignment,
            VerticalAlignment = verticalAlignment,
            VerticalContentAlignment = verticalContentAlignment
        };

        ApplyInteraction(button, background, foreground, pointerOverBackground, pressedBackground);

        if (onClick is not null)
        {
            button.Click += onClick;
        }

        return button;
    }

    internal static void ApplyInteraction(
        Button button,
        Brush background,
        Brush foreground,
        Brush pointerOverBackground,
        Brush pressedBackground)
    {
        var transparentBorder = new SolidColorBrush(Colors.Transparent);
        button.Resources["ButtonBackground"] = background;
        button.Resources["ButtonBackgroundPointerOver"] = pointerOverBackground;
        button.Resources["ButtonBackgroundPressed"] = pressedBackground;
        button.Resources["ButtonForeground"] = foreground;
        button.Resources["ButtonForegroundPointerOver"] = foreground;
        button.Resources["ButtonForegroundPressed"] = foreground;
        button.Resources["ButtonBorderBrush"] = transparentBorder;
        button.Resources["ButtonBorderBrushPointerOver"] = transparentBorder;
        button.Resources["ButtonBorderBrushPressed"] = transparentBorder;
    }
}
