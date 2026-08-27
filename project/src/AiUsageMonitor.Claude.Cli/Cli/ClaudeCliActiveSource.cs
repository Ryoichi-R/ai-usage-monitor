using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Cli;

/// <summary>
/// 公式Claude CLIを隠しscreenで起動し、/usage後の画面を読み取るactive source。
/// OS固有の起動方法（Windows: 隠しconsole、macOS: PTY）はIClaudeScreenSessionFactory経由で注入される。
/// </summary>
public sealed class ClaudeCliActiveSource : IClaudeUsageSource
{
    private static readonly SemaphoreSlim ConsoleExecutionGate = new(1, 1);
    private readonly string? _configuredExecutablePath;
    private readonly IClaudeWorkspaceProvisioner _workspace;
    private readonly IClaudeScreenSessionFactory _sessionFactory;
    private readonly IClaudeExecutableLocator _executableLocator;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _helperTimeout;
    private readonly Func<string, TimeSpan, CancellationToken, Task<ClaudeCliCapabilities>> _probeCapabilities;

    public ClaudeCliActiveSource(
        string? configuredExecutablePath,
        string bridgePath,
        TimeSpan startupTimeout,
        IClaudeWorkspaceProvisioner workspace,
        IClaudeScreenSessionFactory sessionFactory,
        IClaudeExecutableLocator executableLocator,
        Func<string, TimeSpan, CancellationToken, Task<ClaudeCliCapabilities>>? probeCapabilities = null)
    {
        _configuredExecutablePath = configuredExecutablePath;
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgePath);
        _startupTimeout = startupTimeout;
        _helperTimeout = TimeSpan.FromSeconds(Math.Clamp(startupTimeout.TotalSeconds / 3, 2, 10));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _executableLocator = executableLocator ?? throw new ArgumentNullException(nameof(executableLocator));
        _probeCapabilities = probeCapabilities ?? ClaudeCliCapabilityProbe.ProbeAsync;
    }

    public string WorkspacePath => _workspace.WorkspacePath;

    public async Task<ClaudeUsageObservation> RefreshAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        ClaudeExecutableInfo executable = _executableLocator.Resolve(_configuredExecutablePath);
        if (executable.FailureReason is not null)
        {
            UsageAvailability availability = executable.FailureReason == ClaudeScreenFailureCode.ClaudeNotInstalled.ToReasonCode()
                ? UsageAvailability.NotInstalled
                : UsageAvailability.Unsupported;
            return Observation(Failure(startedAt, availability, executable.FailureReason));
        }

        ClaudeCliCapabilities capabilities = await _probeCapabilities(
            executable.Path,
            TimeSpan.FromSeconds(Math.Clamp(_startupTimeout.TotalSeconds, 5, 30)),
            cancellationToken).ConfigureAwait(false);
        if (!capabilities.Supported)
            return Observation(Failure(
                DateTimeOffset.UtcNow,
                UsageAvailability.Unsupported,
                capabilities.FailureReason,
                capabilities.Version ?? executable.Version));

        await ConsoleExecutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunSessionAsync(
                executable.Path,
                capabilities.Version ?? executable.Version,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ConsoleExecutionGate.Release();
        }
    }

    private async Task<ClaudeUsageObservation> RunSessionAsync(
        string executablePath,
        string? version,
        CancellationToken cancellationToken)
    {
        string workspacePath;
        try
        {
            workspacePath = _workspace.EnsureWorkspace();
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            ArgumentException)
        {
            return Observation(Failure(
                DateTimeOffset.UtcNow,
                UsageAvailability.Error,
                ClaudeScreenFailureCode.WorkspaceProvisionFailed.ToReasonCode(),
                version));
        }

        ScreenSessionResult<IClaudeScreenSession> started;
        try
        {
            started = await _sessionFactory.StartAsync(executablePath, workspacePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            IOException)
        {
            return Observation(Failure(
                DateTimeOffset.UtcNow,
                UsageAvailability.Error,
                ClaudeScreenFailureCode.ProcessStartFailed.ToReasonCode(),
                version));
        }
        if (!started.Success || started.Value is null)
        {
            return Observation(Failure(
                DateTimeOffset.UtcNow,
                UsageAvailability.Error,
                (started.ReasonCode ?? ClaudeScreenFailureCode.ProcessStartFailed).ToReasonCode(),
                version));
        }

        IClaudeScreenSession session = started.Value;
        await using (session.ConfigureAwait(false))
        {
            try
            {
                UsageSnapshot result = await WaitUntilReadyAndReadUsageAsync(
                    session,
                    version,
                    cancellationToken).ConfigureAwait(false);
                return Observation(
                    result,
                    result.Availability == UsageAvailability.Available
                        ? ClaudeUsageSourceKind.CliScreen
                        : ClaudeUsageSourceKind.StatusLineActive);
            }
            finally
            {
                try
                {
                    await session.SendEscapeAsync(
                        _helperTimeout,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidOperationException)
                { }
            }
        }
    }

    private async Task<UsageSnapshot> WaitUntilReadyAndReadUsageAsync(
        IClaudeScreenSession session,
        string? version,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _startupTimeout;
        bool usageSent = false;
        UsageSnapshot? lastIncompleteUsageScreen = null;
        // 直近の試行がscreen読取り失敗だったかを記録する。読取り失敗が続いたままdeadlineに
        // 達した場合、汎用のREADY_TIMEOUTではなくCONSOLE_BUFFER_READ_FAILEDを返し、認証は
        // 正常でも取得経路が壊れているケースを区別できるようにする。
        string? lastReadFailureReason = null;
        // 画面confirmationのたびに（helperを経由して）monitor本体の実行ファイルを子processとして
        // 起動し直している。固定100msだと1回のusage取得で数十回spawnしうるため、進捗がない
        // 待ち区間だけ間隔を広げてspawn頻度を下げる。入力直後の遷移確認（usage送信直後・Ready
        // 検出直後）は反応の速さを優先し、変えない。
        int idlePollDelayMs = 250;
        const int MaximumIdlePollDelayMs = 600;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ScreenSessionResult<ScreenSnapshot> screen = await session.ReadScreenAsync(
                _helperTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!screen.Success || screen.Value is null)
            {
                ClaudeScreenFailureCode reasonCode = screen.ReasonCode ?? ClaudeScreenFailureCode.ScreenReadFailed;
                if (reasonCode == ClaudeScreenFailureCode.ProcessExited)
                    return Failure(DateTimeOffset.UtcNow, UsageAvailability.Unavailable, reasonCode.ToReasonCode(), version);
                lastReadFailureReason = reasonCode.ToReasonCode();
                await Task.Delay(idlePollDelayMs, cancellationToken).ConfigureAwait(false);
                idlePollDelayMs = Math.Min(idlePollDelayMs + 100, MaximumIdlePollDelayMs);
                continue;
            }
            lastReadFailureReason = null;

            ClaudeCliScreenSignature signature =
                ClaudeCliScreenStateMachine.Classify(screen.Value.Lines);
            switch (signature)
            {
                case ClaudeCliScreenSignature.TrustPrompt:
                case ClaudeCliScreenSignature.SetupScreen:
                    return Failure(DateTimeOffset.UtcNow, UsageAvailability.Setup, ClaudeScreenFailureCode.ClaudeTrustRequired.ToReasonCode(), version);
                case ClaudeCliScreenSignature.SignedOut:
                    return Failure(DateTimeOffset.UtcNow, UsageAvailability.SignedOut, ClaudeScreenFailureCode.ClaudeSignedOut.ToReasonCode(), version);
                case ClaudeCliScreenSignature.Ready:
                    if (usageSent)
                    {
                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    ScreenSessionResult sent = await session.SendUsageCommandAsync(
                        _helperTimeout,
                        cancellationToken).ConfigureAwait(false);
                    if (!sent.Success)
                        return Failure(
                            DateTimeOffset.UtcNow,
                            UsageAvailability.Error,
                            (sent.ReasonCode ?? ClaudeScreenFailureCode.InputFailed).ToReasonCode(),
                            version);
                    usageSent = true;
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    break;
                case ClaudeCliScreenSignature.UsageScreen:
                    if (!usageSent)
                    {
                        return Failure(
                            DateTimeOffset.UtcNow,
                            UsageAvailability.Unsupported,
                            ClaudeScreenFailureCode.UnexpectedUsageScreen.ToReasonCode(),
                            version);
                    }
                    UsageSnapshot parsed = ClaudeCliUsageScreenParser.Parse(
                        screen.Value.Lines,
                        DateTimeOffset.UtcNow,
                        version);
                    if (parsed.Availability == UsageAvailability.Available ||
                        !string.Equals(
                            parsed.Reason,
                            ClaudeScreenFailureCode.UsageScreenParseFailed.ToReasonCode(),
                            StringComparison.Ordinal))
                    {
                        return parsed;
                    }
                    // Claude Codeは/usageを段階描画する。見出しだけが先に現れたincomplete screenを
                    // 確定失敗にせず、同じbounded session内で再読込する。
                    lastIncompleteUsageScreen = parsed;
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    break;
                case ClaudeCliScreenSignature.Unknown:
                default:
                    await Task.Delay(idlePollDelayMs, cancellationToken).ConfigureAwait(false);
                    idlePollDelayMs = Math.Min(idlePollDelayMs + 100, MaximumIdlePollDelayMs);
                    break;
            }
        }
        return lastIncompleteUsageScreen ??
            Failure(
                DateTimeOffset.UtcNow,
                UsageAvailability.Unavailable,
                lastReadFailureReason is not null
                    ? ClaudeScreenFailureCode.ConsoleBufferReadFailed.ToReasonCode()
                    : ClaudeScreenFailureCode.ReadyTimeout.ToReasonCode(),
                version);
    }

    private static ClaudeUsageObservation Observation(
        UsageSnapshot snapshot,
        ClaudeUsageSourceKind source = ClaudeUsageSourceKind.StatusLineActive) =>
        new(source, snapshot);

    private static UsageSnapshot Failure(
        DateTimeOffset now,
        UsageAvailability availability,
        string? reason,
        string? version = null) =>
        new(
            UsageProvider.Claude,
            now,
            now,
            availability,
            reason,
            null,
            [],
            null,
            false,
            null,
            version);
}
