using System.Text.Encodings.Web;
using System.Text.Json;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Platform;

namespace AiUsageMonitor.Claude.Windows.Process;

public sealed class ClaudeWorkspaceProvisioner : IClaudeWorkspaceProvisioner
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _temporaryDirectory;

    public string WorkspacePath { get; }

    /// <summary>
    /// workspaceと一時settingsの置き場所は<see cref="IAppPathProvider"/>が用途別に解決する。
    /// SpecialFolderの暗黙マッピングへここから直接依存しない（macOSでの解決先差異を吸収するため）。
    /// </summary>
    public ClaudeWorkspaceProvisioner(IAppPathProvider paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        WorkspacePath = paths.ClaudeWorkspaceDirectory;
        _temporaryDirectory = paths.TemporaryDirectory;
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

        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(_temporaryDirectory, $"claude-settings-{Guid.NewGuid():N}.json");
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
