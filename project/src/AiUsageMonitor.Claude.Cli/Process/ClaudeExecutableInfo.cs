namespace AiUsageMonitor.Claude.Cli;

/// <summary>公式install経路から解決したClaude CLI実行ファイルの情報。</summary>
public sealed record ClaudeExecutableInfo(
    string Path,
    string? Version,
    bool SignatureValid,
    string? Publisher,
    string? FailureReason);

/// <summary>公式install経路から署名済みnative Claude CLIを解決する抽象。</summary>
public interface IClaudeExecutableLocator
{
    ClaudeExecutableInfo Resolve(string? configuredPath);
}
