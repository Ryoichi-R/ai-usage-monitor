using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Claude.Windows.Console;

namespace AiUsageMonitor.Claude.Windows.Cli;

/// <summary>
/// HiddenConsoleSession（隠しconsoleで起動したClaude CLIプロセス）とConsoleHelperClient
/// （別プロセスでAttachConsoleする読み書き境界）を束ね、IClaudeScreenSession契約に適合させる。
/// </summary>
public sealed class WindowsClaudeScreenSession : IClaudeScreenSession
{
    private readonly HiddenConsoleSession _session;
    private readonly ConsoleHelperClient _helper;

    public WindowsClaudeScreenSession(HiddenConsoleSession session, ConsoleHelperClient helper)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _helper = helper ?? throw new ArgumentNullException(nameof(helper));
    }

    public async Task<ScreenSessionResult<ScreenSnapshot>> ReadScreenAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_session.Process.HasExited)
            return ScreenSessionResult<ScreenSnapshot>.Fail(ClaudeScreenFailureCode.ProcessExited);
        ConsoleHelperResponse response = await _helper.ReadAsync(_session.Process.Id, timeout, cancellationToken)
            .ConfigureAwait(false);
        return response.Ok
            ? ScreenSessionResult<ScreenSnapshot>.Ok(new ScreenSnapshot(response.Lines, response.Width, response.Height))
            : ScreenSessionResult<ScreenSnapshot>.Fail(ClaudeScreenFailureCodeConvert.FromReasonCode(response.Reason));
    }

    public async Task<ScreenSessionResult> SendUsageCommandAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ConsoleHelperResponse response = await _helper.SendUsageAsync(_session.Process.Id, timeout, cancellationToken)
            .ConfigureAwait(false);
        return ToResult(response);
    }

    public async Task<ScreenSessionResult> SendEscapeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ConsoleHelperResponse response = await _helper.SendEscapeAsync(_session.Process.Id, timeout, cancellationToken)
            .ConfigureAwait(false);
        return ToResult(response);
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();

    private static ScreenSessionResult ToResult(ConsoleHelperResponse response) =>
        response.Ok
            ? ScreenSessionResult.Ok()
            : ScreenSessionResult.Fail(ClaudeScreenFailureCodeConvert.FromReasonCode(response.Reason));
}

/// <summary>実行ファイルとworkspaceから隠しconsoleセッションを起動するWindows実装。</summary>
public sealed class WindowsClaudeScreenSessionFactory : IClaudeScreenSessionFactory
{
    private readonly string? _helperHostPath;

    /// <param name="helperHostPath">
    /// consoleヘルパーを別プロセスとして起動する際のホスト実行ファイル。既定（null）では
    /// 現在のprocessパス（<see cref="Environment.ProcessPath"/>）を使う。テストではfake CLIを渡す。
    /// </param>
    public WindowsClaudeScreenSessionFactory(string? helperHostPath = null)
    {
        _helperHostPath = helperHostPath;
    }

    public Task<ScreenSessionResult<IClaudeScreenSession>> StartAsync(
        string executablePath,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        try
        {
            HiddenConsoleSession session = HiddenConsoleSession.Start(executablePath, workspacePath);
            IClaudeScreenSession wrapped = new WindowsClaudeScreenSession(session, new ConsoleHelperClient(_helperHostPath));
            return Task.FromResult(ScreenSessionResult<IClaudeScreenSession>.Ok(wrapped));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(ScreenSessionResult<IClaudeScreenSession>.Fail(ClaudeScreenFailureCode.ProcessStartFailed));
        }
    }
}
