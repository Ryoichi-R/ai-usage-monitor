using AiUsageMonitor.App.UI.Appearance;
using AiUsageMonitor.Core.Settings;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace AiUsageMonitor.App.UI.Tests;

public sealed class AppearanceTests
{
    [Fact]
    public void DisabledOrZeroOpacityBackgroundIsTransparentWithoutMask()
    {
        Assert.Equal(new AppearanceStyle(ArgbColor.Transparent, null), AppearanceStyle.From(new AppSettings()));
        AppearanceStyle zero = AppearanceStyle.From(new AppSettings
        {
            BackgroundEnabled = true,
            BackgroundOpacity = 0,
            BackgroundFillMode = BackgroundFillMode.EdgeFade,
        });
        Assert.Equal(ArgbColor.Transparent, zero.Fill);
        Assert.NotNull(zero.EdgeFadeMaskRatio);
    }

    [Theory]
    [InlineData("#1E90FF", 0.5, 128, 0x1E, 0x90, 0xFF)]
    [InlineData("#801E90FF", 1.0, 255, 0x1E, 0x90, 0xFF)]
    [InlineData("#abcdef", 0.25, 64, 0xAB, 0xCD, 0xEF)]
    [InlineData("not-a-color", 1.0, 255, 0, 0, 0)]
    public void FillUsesOpaqueRgbAndRoundedOpacity(string color, double opacity, byte a, byte r, byte g, byte b)
    {
        AppearanceStyle style = AppearanceStyle.From(new AppSettings
        {
            BackgroundEnabled = true,
            BackgroundColor = color,
            BackgroundOpacity = opacity,
        });

        Assert.Equal(new ArgbColor(a, r, g, b), style.Fill);
        Assert.Null(style.EdgeFadeMaskRatio);
    }

    [Theory]
    [InlineData(22, 0.22 / 1.44)]
    [InlineData(5, 0.05 / 1.10)]
    [InlineData(50, 0.5 / 2.0)]
    [InlineData(80, 0.5 / 2.0)]
    public void EdgeFadeMaskRatioMatchesExpandedGeometry(double percent, double expected)
    {
        AppearanceStyle style = AppearanceStyle.From(new AppSettings
        {
            BackgroundEnabled = true,
            BackgroundFillMode = BackgroundFillMode.EdgeFade,
            BackgroundEdgeFadePercent = percent,
        });

        Assert.Equal(expected, style.EdgeFadeMaskRatio!.Value, 10);
    }

    [AvaloniaFact]
    public void EdgeFadeMaskFadesBothEndsOnBothAxes()
    {
        var horizontalHost = new Grid();
        var surface = new Border();
        AppSettings settings = new()
        {
            BackgroundEnabled = true,
            BackgroundFillMode = BackgroundFillMode.EdgeFade,
            BackgroundEdgeFadePercent = 22,
        };

        AvaloniaAppearanceBrushes.Apply(horizontalHost, surface, settings);

        var horizontal = Assert.IsAssignableFrom<IGradientBrush>(horizontalHost.OpacityMask);
        var vertical = Assert.IsAssignableFrom<IGradientBrush>(surface.OpacityMask);
        Assert.Equal(4, horizontal.GradientStops.Count);
        Assert.Equal(4, vertical.GradientStops.Count);
        Assert.Equal(0, horizontal.GradientStops[0].Color.A);
        Assert.Equal(0, horizontal.GradientStops[^1].Color.A);
        Assert.Equal(0, vertical.GradientStops[0].Color.A);
        Assert.Equal(0, vertical.GradientStops[^1].Color.A);
        Assert.True(horizontal.GradientStops[1].Color.A > 0);
        Assert.True(vertical.GradientStops[2].Color.A > 0);
        Assert.Equal(0.22 / 1.44, horizontal.GradientStops[1].Offset, 10);
        Assert.Equal(1 - (0.22 / 1.44), vertical.GradientStops[2].Offset, 10);
    }

    [AvaloniaFact]
    public void SolidAndDisabledBackgroundClearAllMasks()
    {
        var horizontalHost = new Grid();
        var surface = new Border();
        AppSettings settings = new()
        {
            BackgroundEnabled = true,
            BackgroundFillMode = BackgroundFillMode.Solid,
            BackgroundColor = "#102030",
            BackgroundOpacity = 1,
        };

        AvaloniaAppearanceBrushes.Apply(horizontalHost, surface, settings);
        Assert.Null(horizontalHost.OpacityMask);
        Assert.Null(surface.OpacityMask);
        var fill = Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background);
        Assert.Equal(Color.FromArgb(255, 0x10, 0x20, 0x30), fill.Color);

        AvaloniaAppearanceBrushes.Apply(horizontalHost, surface, settings with { BackgroundEnabled = false });
        Assert.Null(horizontalHost.OpacityMask);
        Assert.Null(surface.OpacityMask);
        Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color.A);
    }

    [AvaloniaFact]
    public void ClearRemovesMasksAndMakesSurfaceTransparent()
    {
        var horizontalHost = new Grid();
        var surface = new Border();
        AvaloniaAppearanceBrushes.Apply(horizontalHost, surface, new AppSettings
        {
            BackgroundEnabled = true,
            BackgroundFillMode = BackgroundFillMode.EdgeFade,
        });

        AvaloniaAppearanceBrushes.Clear(horizontalHost, surface);

        Assert.Null(horizontalHost.OpacityMask);
        Assert.Null(surface.OpacityMask);
        Assert.Same(Brushes.Transparent, surface.Background);
    }
}
