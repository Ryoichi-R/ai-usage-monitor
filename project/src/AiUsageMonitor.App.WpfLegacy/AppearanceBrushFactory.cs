using AiUsageMonitor.Core.Settings;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;

namespace AiUsageMonitor.App;

internal static class AppearanceBrushFactory
{
    internal static void Apply(
        FrameworkElement horizontalFadeHost,
        Border backgroundSurface,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(horizontalFadeHost);
        ArgumentNullException.ThrowIfNull(backgroundSurface);
        ArgumentNullException.ThrowIfNull(settings);

        AppSettings normalized = settings.Normalized();
        backgroundSurface.Background = CreateFill(normalized);
        if (!normalized.BackgroundEnabled || normalized.BackgroundFillMode != BackgroundFillMode.EdgeFade)
        {
            horizontalFadeHost.OpacityMask = null;
            backgroundSurface.OpacityMask = null;
            return;
        }

        double ratio = Math.Clamp(normalized.BackgroundEdgeFadePercent / 100d, 0.05, 0.5);
        double maskRatio = ratio / (1d + (2d * ratio));
        horizontalFadeHost.OpacityMask = CreateHorizontalMask(maskRatio);
        backgroundSurface.OpacityMask = CreateVerticalMask(maskRatio);
    }

    internal static void Clear(FrameworkElement horizontalFadeHost, Border backgroundSurface)
    {
        ArgumentNullException.ThrowIfNull(horizontalFadeHost);
        ArgumentNullException.ThrowIfNull(backgroundSurface);
        horizontalFadeHost.OpacityMask = null;
        backgroundSurface.OpacityMask = null;
        backgroundSurface.Background = System.Windows.Media.Brushes.Transparent;
    }

    internal static MediaBrush CreateFill(AppSettings settings)
    {
        AppSettings normalized = settings.Normalized();
        if (!normalized.BackgroundEnabled || normalized.EffectiveBackgroundOpacity <= 0)
            return Freeze(new SolidColorBrush(Colors.Transparent));

        System.Windows.Media.Color rgb = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
            "#" + normalized.EffectiveBackgroundRgb);
        byte alpha = (byte)Math.Round(
            Math.Clamp(normalized.EffectiveBackgroundOpacity, 0, 1) * byte.MaxValue,
            MidpointRounding.AwayFromZero);
        return Freeze(new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B)));
    }

    private static LinearGradientBrush CreateHorizontalMask(double fade)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, .5),
            EndPoint = new System.Windows.Point(1, .5),
        };
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, 255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(255, 255, 255, 255), fade));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(255, 255, 255, 255), 1 - fade));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, 255, 255, 255), 1));
        return Freeze(brush);
    }

    private static LinearGradientBrush CreateVerticalMask(double fade)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(.5, 0),
            EndPoint = new System.Windows.Point(.5, 1),
        };
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, 255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(255, 255, 255, 255), fade));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(255, 255, 255, 255), 1 - fade));
        brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, 255, 255, 255), 1));
        return Freeze(brush);
    }

    private static T Freeze<T>(T brush) where T : MediaBrush
    {
        brush.Freeze();
        return brush;
    }
}
