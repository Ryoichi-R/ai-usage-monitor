using AiUsageMonitor.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace AiUsageMonitor.App.UI.Appearance;

/// <summary>
/// <see cref="AppearanceStyle"/>をAvaloniaのbrushへ適用する。WPF版<c>AppearanceBrushFactory</c>の
/// Avalonia移植で、Freezeの代わりにimmutable brushを使う。
/// </summary>
internal static class AvaloniaAppearanceBrushes
{
    internal static void Apply(Control horizontalFadeHost, Border backgroundSurface, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(horizontalFadeHost);
        ArgumentNullException.ThrowIfNull(backgroundSurface);
        ArgumentNullException.ThrowIfNull(settings);

        AppearanceStyle style = AppearanceStyle.From(settings);
        backgroundSurface.Background = CreateFill(style);
        if (style.EdgeFadeMaskRatio is not { } fade)
        {
            horizontalFadeHost.OpacityMask = null;
            backgroundSurface.OpacityMask = null;
            return;
        }

        horizontalFadeHost.OpacityMask = CreateMask(fade, horizontal: true);
        backgroundSurface.OpacityMask = CreateMask(fade, horizontal: false);
    }

    internal static void Clear(Control horizontalFadeHost, Border backgroundSurface)
    {
        ArgumentNullException.ThrowIfNull(horizontalFadeHost);
        ArgumentNullException.ThrowIfNull(backgroundSurface);
        horizontalFadeHost.OpacityMask = null;
        backgroundSurface.OpacityMask = null;
        backgroundSurface.Background = Brushes.Transparent;
    }

    internal static IImmutableSolidColorBrush CreateFill(AppearanceStyle style)
    {
        ArgbColor fill = style.Fill;
        return new ImmutableSolidColorBrush(Color.FromArgb(fill.A, fill.R, fill.G, fill.B));
    }

    private static IBrush CreateMask(double fade, bool horizontal)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = horizontal
                ? new RelativePoint(0, .5, RelativeUnit.Relative)
                : new RelativePoint(.5, 0, RelativeUnit.Relative),
            EndPoint = horizontal
                ? new RelativePoint(1, .5, RelativeUnit.Relative)
                : new RelativePoint(.5, 1, RelativeUnit.Relative),
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), fade));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), 1 - fade));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
        return brush.ToImmutable();
    }
}
