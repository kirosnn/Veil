using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Veil.Interop.NativeMethods;

namespace Veil.Windows;

internal static class ReadableSurfaceHelper
{
    internal static bool ShouldUseDarkForeground(int left, int top, int width, int height)
    {
        IntPtr screenDc = GetWindowDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            int right = left + width;
            int bottom = top + height;

            int[][] samplePoints =
            [
                [left + (width / 2), top + (height / 2)],
                [left + (width / 4), top + (height / 4)],
                [left + ((width * 3) / 4), top + (height / 4)],
                [left + (width / 4), top + ((height * 3) / 4)],
                [left + ((width * 3) / 4), top + ((height * 3) / 4)]
            ];

            double luminanceSum = 0;
            int sampleCount = 0;

            foreach (int[] point in samplePoints)
            {
                int x = Math.Clamp(point[0], left, Math.Max(left, right - 1));
                int y = Math.Clamp(point[1], top, Math.Max(top, bottom - 1));
                uint colorValue = GetPixel(screenDc, x, y);
                if (colorValue == 0xFFFFFFFF)
                {
                    continue;
                }

                byte r = (byte)(colorValue & 0xFF);
                byte g = (byte)((colorValue >> 8) & 0xFF);
                byte b = (byte)((colorValue >> 16) & 0xFF);
                luminanceSum += GetRelativeLuminance(r, g, b);
                sampleCount++;
            }

            if (sampleCount == 0)
            {
                return false;
            }

            return (luminanceSum / sampleCount) >= 0.72;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    internal static SolidColorBrush CreateTextBrush(bool useDarkForeground, byte alpha = 255)
    {
        return new SolidColorBrush(useDarkForeground
            ? global::Windows.UI.Color.FromArgb((byte)Math.Max((int)alpha, 220), 18, 18, 22)
            : global::Windows.UI.Color.FromArgb(alpha, 255, 255, 255));
    }

    internal static void ApplyTextContrast(DependencyObject root, bool useDarkForeground)
    {
        ApplyTextContrastCore(root, useDarkForeground);
    }

    private static void ApplyTextContrastCore(DependencyObject node, bool useDarkForeground)
    {
        if (node is TextBlock textBlock)
        {
            textBlock.Foreground = CreateTextBrush(useDarkForeground, GetAlpha(textBlock.Foreground, 255));
        }
        else if (node is Button button)
        {
            button.Foreground = CreateTextBrush(useDarkForeground, GetAlpha(button.Foreground, 255));
        }

        int childrenCount = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < childrenCount; i++)
        {
            ApplyTextContrastCore(VisualTreeHelper.GetChild(node, i), useDarkForeground);
        }
    }

    private static byte GetAlpha(Brush? brush, byte fallbackAlpha)
    {
        return brush is SolidColorBrush solidColorBrush ? solidColorBrush.Color.A : fallbackAlpha;
    }

    private static double GetRelativeLuminance(byte r, byte g, byte b)
    {
        return ((0.2126 * r) + (0.7152 * g) + (0.0722 * b)) / 255.0;
    }
}
