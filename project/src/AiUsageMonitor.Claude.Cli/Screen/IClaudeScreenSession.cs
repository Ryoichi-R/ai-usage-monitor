namespace AiUsageMonitor.Claude.Cli;

/// <summary>
/// 起動済みClaude CLIプロセスの隠しscreenに対するread/usage/escape操作の抽象。
/// Windows実装はHiddenConsoleSession + ConsoleHelperClient（別プロセスでAttachConsole）、
/// macOS実装はopenpty + VTスクリーンモデルで、行×桁の解決済みテキストへ落とし込む。
/// </summary>
public interface IClaudeScreenSession : IAsyncDisposable
{
    Task<ScreenSessionResult<ScreenSnapshot>> ReadScreenAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>"/usage" + Enterを送出する。</summary>
    Task<ScreenSessionResult> SendUsageCommandAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Escapeを送出する。</summary>
    Task<ScreenSessionResult> SendEscapeAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// 実行ファイルとworkspaceからIClaudeScreenSessionを起動するファクトリ。
/// ClaudeCliActiveSourceはこれを注入されることで、new ConsoleHelperClient()等の
/// 具象生成を共通層で行わない。
/// </summary>
public interface IClaudeScreenSessionFactory
{
    Task<ScreenSessionResult<IClaudeScreenSession>> StartAsync(
        string executablePath,
        string workspacePath,
        CancellationToken cancellationToken);
}
