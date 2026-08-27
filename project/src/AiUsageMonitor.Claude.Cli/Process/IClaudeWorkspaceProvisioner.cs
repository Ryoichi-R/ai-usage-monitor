namespace AiUsageMonitor.Claude.Cli;

/// <summary>Claude CLIを起動する専用workspaceを用意する抽象。</summary>
public interface IClaudeWorkspaceProvisioner
{
    string WorkspacePath { get; }

    string EnsureWorkspace();
}
