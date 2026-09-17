namespace AiUsageMonitor.App.Tests;

public sealed class MonitorChoiceTests
{
    private static readonly MonitorChoice[] Connected =
    [
        MonitorChoice.ForScreen(@"\\.\DISPLAY1", "MONITOR-B", primary: false),
        MonitorChoice.ForScreen(@"\\.\DISPLAY2", "MONITOR-P", primary: true),
    ];

    [Fact]
    public void StableIdSelectsRenumberedMonitor()
    {
        MonitorChoice choice = MonitorChoice.Select(Connected, @"\\.\DISPLAY7", "monitor-b");

        Assert.Same(Connected[0], choice);
    }

    [Fact]
    public void MissingStableIdKeepsSavedMonitorAsDisconnectedChoice()
    {
        MonitorChoice choice = MonitorChoice.Select(Connected, @"\\.\DISPLAY2", "MONITOR-GONE");

        Assert.True(choice.IsDisconnected);
        Assert.Equal("MONITOR-GONE", choice.StableId);
        Assert.Equal(@"\\.\DISPLAY2", choice.DeviceName);
        Assert.Equal(MonitorChoice.DisconnectedDisplayName, choice.ToString());
    }

    [Fact]
    public void LegacyDeviceNameSelectsConnectedMonitorAndCarriesStableId()
    {
        MonitorChoice choice = MonitorChoice.Select(Connected, @"\\.\display1", null);

        Assert.Same(Connected[0], choice);
        Assert.Equal("MONITOR-B", choice.StableId);
    }

    [Theory]
    [InlineData(@"\\.\DISPLAY7")]
    [InlineData(null)]
    [InlineData("")]
    public void UnknownLegacyOrAutomaticSelectionFallsBackToAutomatic(string? deviceName)
    {
        MonitorChoice choice = MonitorChoice.Select(Connected, deviceName, null);

        Assert.Same(MonitorChoice.Automatic, choice);
    }

    [Fact]
    public void ScreenChoiceMarksPrimary()
    {
        Assert.Equal(@"\\.\DISPLAY2（プライマリ）", Connected[1].ToString());
        Assert.Equal(@"\\.\DISPLAY1", Connected[0].ToString());
    }
}
