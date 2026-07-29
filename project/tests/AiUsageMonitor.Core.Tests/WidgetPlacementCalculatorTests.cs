using AiUsageMonitor.Core.Settings;

namespace AiUsageMonitor.Core.Tests;

public sealed class WidgetPlacementCalculatorTests
{
    // 1920x1040 相当の作業領域（タスクバー分を下端で除いた想定）。原点はマルチモニターを意識して非0にする。
    private static readonly WorkArea Area = new(100, 50, 1920, 1040);

    [Theory]
    [InlineData(PlacementAnchor.TopRight, 1920 + 100 - 280 - 12, 50 + 12)]
    [InlineData(PlacementAnchor.TopLeft, 100 + 12, 50 + 12)]
    [InlineData(PlacementAnchor.BottomRight, 1920 + 100 - 280 - 12, 50 + 1040 - 400 - 12)]
    [InlineData(PlacementAnchor.BottomLeft, 100 + 12, 50 + 1040 - 400 - 12)]
    public void CalculatePresetAnchorKeepsMargin(PlacementAnchor anchor, double expectedLeft, double expectedTop)
    {
        var settings = new AppSettings
        {
            PlacementMode = PlacementMode.Preset,
            Anchor = anchor,
            HorizontalMarginDip = 12,
            VerticalMarginDip = 12,
        };

        WidgetPlacement placement = WidgetPlacementCalculator.Calculate(Area, 280, 400, settings);

        Assert.Equal(expectedLeft, placement.Left, 3);
        Assert.Equal(expectedTop, placement.Top, 3);
    }

    [Theory]
    [InlineData(0d, 0d, 100, 50)]
    [InlineData(1d, 1d, 100 + 1920 - 280, 50 + 1040 - 400)]
    [InlineData(0.5, 0.5, 100 + ((1920 - 280) / 2d), 50 + ((1040 - 400) / 2d))]
    public void CalculateCustomFractionMapsIntoArea(double leftFraction, double topFraction, double expectedLeft, double expectedTop)
    {
        var settings = new AppSettings
        {
            PlacementMode = PlacementMode.Custom,
            CustomLeftFraction = leftFraction,
            CustomTopFraction = topFraction,
        };

        WidgetPlacement placement = WidgetPlacementCalculator.Calculate(Area, 280, 400, settings);

        Assert.Equal(expectedLeft, placement.Left, 3);
        Assert.Equal(expectedTop, placement.Top, 3);
    }

    [Fact]
    public void CalculateCustomWithoutFractionFallsBackToPreset()
    {
        var settings = new AppSettings
        {
            PlacementMode = PlacementMode.Custom,
            CustomLeftFraction = null,
            CustomTopFraction = null,
            Anchor = PlacementAnchor.TopRight,
            HorizontalMarginDip = 12,
            VerticalMarginDip = 12,
        };

        WidgetPlacement placement = WidgetPlacementCalculator.Calculate(Area, 280, 400, settings);

        Assert.Equal(1920 + 100 - 280 - 12, placement.Left, 3);
        Assert.Equal(50 + 12, placement.Top, 3);
    }

    [Fact]
    public void CalculateWidgetTallerThanAreaClampsTopToAreaTop()
    {
        // 200% かつ両表示相当で、外形高だけが作業領域高を超えるケース（幅は収まる）。
        var small = new WorkArea(100, 50, 600, 690);
        var settings = new AppSettings
        {
            PlacementMode = PlacementMode.Preset,
            Anchor = PlacementAnchor.BottomRight,
            HorizontalMarginDip = 12,
            VerticalMarginDip = 12,
        };

        WidgetPlacement placement = WidgetPlacementCalculator.Calculate(small, 560, 820, settings);

        // 高さは収まらないため余白よりclampを優先し上辺を作業領域に合わせる。幅は収まるので余白どおり。
        Assert.Equal(small.Top, placement.Top, 3);
        Assert.Equal(small.Right - 560 - 12, placement.Left, 3);
    }

    [Fact]
    public void CalculateWidgetWiderThanAreaClampsLeftToAreaLeft()
    {
        var narrow = new WorkArea(100, 50, 400, 1040);
        var settings = new AppSettings
        {
            PlacementMode = PlacementMode.Preset,
            Anchor = PlacementAnchor.TopRight,
            HorizontalMarginDip = 12,
            VerticalMarginDip = 12,
        };

        WidgetPlacement placement = WidgetPlacementCalculator.Calculate(narrow, 560, 400, settings);

        Assert.Equal(narrow.Left, placement.Left, 3);
    }

    [Fact]
    public void ClampToAreaInsidePositionUnchanged()
    {
        WidgetPlacement placement = WidgetPlacementCalculator.ClampToArea(Area, 280, 400, 500, 300);

        Assert.Equal(500, placement.Left, 3);
        Assert.Equal(300, placement.Top, 3);
    }

    [Fact]
    public void ClampToAreaBeyondRightBottomPulledInside()
    {
        WidgetPlacement placement = WidgetPlacementCalculator.ClampToArea(Area, 280, 400, 10_000, 10_000);

        Assert.Equal(Area.Right - 280, placement.Left, 3);
        Assert.Equal(Area.Bottom - 400, placement.Top, 3);
    }

    [Fact]
    public void ClampToAreaBeforeLeftTopPulledInside()
    {
        WidgetPlacement placement = WidgetPlacementCalculator.ClampToArea(Area, 280, 400, -10_000, -10_000);

        Assert.Equal(Area.Left, placement.Left, 3);
        Assert.Equal(Area.Top, placement.Top, 3);
    }

    [Fact]
    public void CalculateZeroMarginAndBoundaryFractionsProduceFiniteValues()
    {
        var edge = new WorkArea(0, 0, 280, 400);
        var settings = new AppSettings
        {
            PlacementMode = PlacementMode.Custom,
            CustomLeftFraction = 1,
            CustomTopFraction = 1,
            HorizontalMarginDip = 0,
            VerticalMarginDip = 0,
        };

        WidgetPlacement placement = WidgetPlacementCalculator.Calculate(edge, 280, 400, settings);

        // 作業領域と外形が一致し可動域0のため、原点に収まる。NaN/例外が出ないこと。
        Assert.True(double.IsFinite(placement.Left));
        Assert.True(double.IsFinite(placement.Top));
        Assert.Equal(0, placement.Left, 3);
        Assert.Equal(0, placement.Top, 3);
    }
}
