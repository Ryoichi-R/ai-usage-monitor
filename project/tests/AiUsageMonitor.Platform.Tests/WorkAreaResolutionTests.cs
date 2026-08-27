using AiUsageMonitor.Platform;

namespace AiUsageMonitor.Platform.Tests;

public sealed class WorkAreaResolutionTests
{
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
