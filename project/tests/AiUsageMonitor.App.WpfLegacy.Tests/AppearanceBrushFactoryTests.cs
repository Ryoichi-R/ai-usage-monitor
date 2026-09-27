using System.Windows.Controls;
using System.Windows.Media;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.App;

namespace AiUsageMonitor.App.Tests;

public sealed class AppearanceBrushFactoryTests
{
    [Fact]
    public void EdgeFadeMaskFadesBothEndsOnBothAxes() => MainWindowScaleTestSupport.RunInSta(() =>
    {
        var horizontalHost = new Grid();
        var surface = new Border();
        AppSettings settings = new()
        {
            BackgroundEnabled = true,
            BackgroundFillMode = BackgroundFillMode.EdgeFade,
            BackgroundEdgeFadePercent = 22,
        };

        AppearanceBrushFactory.Apply(horizontalHost, surface, settings);

        LinearGradientBrush horizontal = Assert.IsType<LinearGradientBrush>(horizontalHost.OpacityMask);
        LinearGradientBrush vertical = Assert.IsType<LinearGradientBrush>(surface.OpacityMask);
        Assert.Equal(4, horizontal.GradientStops.Count);
        Assert.Equal(4, vertical.GradientStops.Count);
        Assert.Equal(0, horizontal.GradientStops[0].Color.A);
        Assert.Equal(0, horizontal.GradientStops[^1].Color.A);
        Assert.Equal(0, vertical.GradientStops[0].Color.A);
        Assert.Equal(0, vertical.GradientStops[^1].Color.A);
        Assert.True(horizontal.GradientStops[1].Color.A > 0);
        Assert.True(vertical.GradientStops[2].Color.A > 0);
    });

    [Fact]
    public void SolidAndDisabledBackgroundClearAllMasks() => MainWindowScaleTestSupport.RunInSta(() =>
    {
        var horizontalHost = new Grid();
        var surface = new Border();
        AppSettings settings = new() { BackgroundEnabled = true, BackgroundFillMode = BackgroundFillMode.Solid };

        AppearanceBrushFactory.Apply(horizontalHost, surface, settings);
        Assert.Null(horizontalHost.OpacityMask);
        Assert.Null(surface.OpacityMask);

        AppearanceBrushFactory.Apply(horizontalHost, surface, settings with { BackgroundEnabled = false });
        Assert.Null(horizontalHost.OpacityMask);
        Assert.Null(surface.OpacityMask);
    });
}
