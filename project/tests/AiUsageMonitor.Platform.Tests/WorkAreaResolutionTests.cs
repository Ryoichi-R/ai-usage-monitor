using AiUsageMonitor.Platform;

namespace AiUsageMonitor.Platform.Tests;

public sealed class WorkAreaResolutionTests
{
    [Fact]
    public void PlatformContractValuesExposeTheirFields()
    {
        var area = new WidgetWorkArea("DISPLAY1", -10, 20, 1920, 1080, 1.5, 1.5);
        Assert.Equal("DISPLAY1", area.DisplayId);
        Assert.Equal(-10, area.Left);
        Assert.Equal(20, area.Top);
        Assert.Equal(1920, area.Width);
        Assert.Equal(1080, area.Height);
        Assert.Equal(1.5, area.ScaleX);
        Assert.Equal(1.5, area.ScaleY);

        var trust = new ExecutableTrustResult(true, "publisher", null);
        Assert.True(trust.Valid);
        Assert.Equal("publisher", trust.Publisher);
        Assert.Null(trust.FailureReason);

        var health = new WidgetLayerHealth(true, "SetLayerMode", 5);
        Assert.True(health.IsDegraded);
        Assert.Equal("SetLayerMode", health.Operation);
        Assert.Equal(5, health.ErrorCode);
    }

    [Fact]
    public void SuccessCarriesTheProvidedAreaWithoutFailureDetails()
    {
        var area = new WidgetWorkArea("DISPLAY1", 0, 0, 1920, 1080, 1.0, 1.0);

        WorkAreaResolution result = WorkAreaResolution.Success(area);

        Assert.True(result.Succeeded);
        Assert.Equal(area, result.Area);
        Assert.Null(result.FailureStage);
        Assert.Equal(0, result.ErrorCode);
    }

    [Fact]
    public void FailureCarriesTheStageAndErrorCodeWithoutAnArea()
    {
        WorkAreaResolution result = WorkAreaResolution.Failure("EnumDisplayMonitors", 87);

        Assert.False(result.Succeeded);
        Assert.Equal("EnumDisplayMonitors", result.FailureStage);
        Assert.Equal(87, result.ErrorCode);
        Assert.Equal(default, result.Area);
    }

    [Fact]
    public void FailureDefaultsErrorCodeToZero()
    {
        WorkAreaResolution result = WorkAreaResolution.Failure("GetMonitorInfo");

        Assert.Equal(0, result.ErrorCode);
    }
}
