using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Platform;

namespace AiUsageMonitor.App.UI.Hosting;

/// <summary>
/// <see cref="IAppPathProvider"/>が解決した専用workspaceを用意するOS共通の実装。
/// Unixでは他ユーザーから読めないよう0700で作る。
/// </summary>
public sealed class AppPathClaudeWorkspace : IClaudeWorkspaceProvisioner
{
    public AppPathClaudeWorkspace(IAppPathProvider paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        WorkspacePath = Path.GetFullPath(paths.ClaudeWorkspaceDirectory);
    }

    public string WorkspacePath { get; }

    public string EnsureWorkspace()
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(WorkspacePath);
        else
            Directory.CreateDirectory(WorkspacePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return WorkspacePath;
    }
}
