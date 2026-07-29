using System.Text.Encodings.Web;
using System.Text.Json;

namespace AiUsageMonitor.Claude.Windows.Process;

public sealed class ClaudeWorkspaceProvisioner
{
    // Keep the trusted workspace path stable across the product rename.
    private const string LegacyAppDataDirectoryName = "CodexUsageMonitor";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string WorkspacePath { get; }

    public ClaudeWorkspaceProvisioner(string? localAppData = null)
    {
        string root = localAppData ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        WorkspacePath = Path.Combine(
            root,
            LegacyAppDataDirectoryName,
            "ClaudeCliWorkspace");
    }

    public string EnsureWorkspace()
    {
        Directory.CreateDirectory(WorkspacePath);
        return Path.GetFullPath(WorkspacePath);
    }

    public TemporaryClaudeSettings CreateSettings(string bridgePath, string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        string fullBridge = Path.GetFullPath(bridgePath);
        if (!File.Exists(fullBridge))
            throw new FileNotFoundException("Claude statusLine bridge was not found.", fullBridge);
        if (fullBridge.Contains('"', StringComparison.Ordinal) ||
            pipeName.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("Unsafe bridge path or pipe name.");

        string directory = Path.Combine(
            Path.GetDirectoryName(WorkspacePath)!,
            "Temp");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"claude-settings-{Guid.NewGuid():N}.json");
        string command =
            $"powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{fullBridge}\" -PipeName {pipeName}";
        var settings = new
        {
            statusLine = new
            {
                type = "command",
                command,
                refreshInterval = 10,
            },
        };
        string json = JsonSerializer.Serialize(settings, SerializerOptions);
        File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
        return new(path);
    }
}

public sealed class TemporaryClaudeSettings : IDisposable
{
    public TemporaryClaudeSettings(string path) => Path = path;

    public string Path { get; }

    public void Dispose()
    {
        try { File.Delete(Path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
