using AiUsageMonitor.Claude.Mac;
namespace AiUsageMonitor.Claude.Bridge.Mac;

public static class BridgeEntry
{
    public static async Task<int> RunAsync(Stream input)
    {
        if (OperatingSystem.IsMacOS()) await ClaudeStatusLineBridge.RunAsync(input).ConfigureAwait(false);
        return 0;
    }
}
