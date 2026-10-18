using System.Text.Json;
using AiUsageMonitor.Claude.Configuration;

namespace AiUsageMonitor.Claude.Tests;

public sealed class ClaudeSetupExampleTests
{
    [Fact]
    public void CreatesEscapedFixedBridgeCommand()
    {
        string json = ClaudeSetupExample.Create(@"C:\Program Files\日本語");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement statusLine = document.RootElement.GetProperty("statusLine");

        Assert.Equal(
            "powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"C:/Program Files/日本語/claude-statusline-bridge.ps1\"",
            statusLine.GetProperty("command").GetString());
        Assert.Equal(30, statusLine.GetProperty("refreshInterval").GetInt32());
    }
}
