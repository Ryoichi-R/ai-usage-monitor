using AiUsageMonitor.Platform.Windows.Startup;

namespace AiUsageMonitor.Platform.Windows.Tests;

public sealed class StartupRegistryServiceTests
{
    [Fact]
    public void PublishedExecutableUsesQuotedProcessPath()
    {
        string command = StartupRegistryService.CreateCommand(
            @"C:\Program Files\AI Usage Monitor\AiUsageMonitor.App.exe",
            @"C:\Program Files\AI Usage Monitor\AiUsageMonitor.App.dll");

        Assert.Equal("\"C:\\Program Files\\AI Usage Monitor\\AiUsageMonitor.App.exe\"", command);
    }

    [Fact]
    public void DotnetHostIncludesEntryAssembly()
    {
        string command = StartupRegistryService.CreateCommand(
            @"C:\Program Files\dotnet\dotnet.exe",
            @"C:\src\AI Usage Monitor\AiUsageMonitor.App.dll");

        Assert.Equal(
            "\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\src\\AI Usage Monitor\\AiUsageMonitor.App.dll\"",
            command);
    }

    [Fact]
    public void DotnetHostWithoutEntryAssemblyUsesOnlyProcessPath()
    {
        string command = StartupRegistryService.CreateCommand(
            @"C:\Program Files\dotnet\dotnet.exe",
            null);

        Assert.Equal("\"C:\\Program Files\\dotnet\\dotnet.exe\"", command);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyProcessPathIsRejected(string processPath) =>
        Assert.Throws<ArgumentException>(() =>
            StartupRegistryService.CreateCommand(processPath, null));

    [Fact]
    public void QuoteInProcessPathIsRejected() =>
        Assert.Throws<ArgumentException>(() =>
            StartupRegistryService.CreateCommand("C:\\bad\"path\\app.exe", null));
}
