using System.Text.Json;

namespace AiUsageMonitor.Claude.Configuration;

public static class ClaudeSetupExample
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Create(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        string directory = baseDirectory.Replace('\\', '/').TrimEnd('/');
        return JsonSerializer.Serialize(new
        {
            statusLine = new
            {
                type = "command",
                command = $"powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{directory}/claude-statusline-bridge.ps1\"",
                refreshInterval = 30,
            }
        }, JsonOptions);
    }
}
