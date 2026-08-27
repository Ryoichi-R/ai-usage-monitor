using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Cli.Tests;

public sealed class ClaudeCliActiveSourceTests
{
    private static readonly string[] ReadyScreen =
    [
        "╭──────────────────────────╮",
        "│ >                        │",
        "╰──────────────────────────╯",
        "? for shortcuts",
    ];

    // ClaudeCliActiveSourceはDateTimeOffset.UtcNowを観測時刻として解析するため、固定文字列の
    // Resets時刻はテスト実行時刻によって有効期限（5時間 / 7日ホライズン）を外れうる。
    // 実行時点からの相対時刻で生成し、いつ実行しても有効な範囲に収める。
    private static string[] UsageScreen(int sessionPercent = 48, int weekPercent = 32)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9));
        return
        [
            "Current session",
            $"{sessionPercent}% used",
            $"Resets {now.AddHours(1).ToString("htt", System.Globalization.CultureInfo.InvariantCulture)} (Asia/Tokyo)",
            "Current week (all models)",
            $"{weekPercent}% used",
            $"Resets {now.AddDays(3).ToString("MMM d, htt", System.Globalization.CultureInfo.InvariantCulture)} (Asia/Tokyo)",
            "What's contributing",
        ];
    }

    [Fact]
    public async Task RefreshAsyncReturnsAvailableAfterReadyThenUsageScreen()
    {
        var session = new FakeClaudeScreenSession(
            ScreenOf(ReadyScreen),
            ScreenOf(UsageScreen()));
        ClaudeCliActiveSource source = CreateSource(session: session);

        ClaudeUsageObservation observation = await source.RefreshAsync(CancellationToken.None);

        Assert.Equal(UsageAvailability.Available, observation.Availability);
        Assert.Equal(ClaudeUsageSourceKind.CliScreen, observation.Source);
        Assert.Equal([48d, 32d], observation.Snapshot.Windows.Select(w => w.UsedPercent));
        Assert.Contains(session.Calls, call => call.Command == "usage");
        Assert.Contains(session.Calls, call => call.Command == "escape");
    }

    [Fact]
    public async Task RefreshAsyncMapsTrustPromptToSetup()
    {
        var session = new FakeClaudeScreenSession(ScreenOf(["Is this a project you created or one you trust?"]));
        ClaudeCliActiveSource source = CreateSource(session: session);

        ClaudeUsageObservation observation = await source.RefreshAsync(CancellationToken.None);

        Assert.Equal(UsageAvailability.Setup, observation.Availability);
        Assert.Equal("CLAUDE_TRUST_REQUIRED", observation.Reason);
    }

    [Fact]
    public async Task RefreshAsyncMapsSignedOut()
    {
        var session = new FakeClaudeScreenSession(ScreenOf(["Sign in to Claude"]));
        ClaudeCliActiveSource source = CreateSource(session: session);

        ClaudeUsageObservation observation = await source.RefreshAsync(CancellationToken.None);

        Assert.Equal(UsageAvailability.SignedOut, observation.Availability);
        Assert.Equal("CLAUDE_SIGNED_OUT", observation.Reason);
    }

    [Fact]
    public async Task RefreshAsyncRejectsUnexpectedUsageScreenBeforeUsageWasSent()
    {
        var session = new FakeClaudeScreenSession(ScreenOf(UsageScreen()));
        ClaudeCliActiveSource source = CreateSource(session: session);

        ClaudeUsageObservation observation = await source.RefreshAsync(CancellationToken.None);

        Assert.Equal(UsageAvailability.Unsupported, observation.Availability);
        Assert.Equal("UNEXPECTED_USAGE_SCREEN", observation.Reason);
    }

    [Fact]
    public async Task RefreshAsyncMapsProcessExitedDuringRead()
    {
        var session = new FakeClaudeScreenSession(failureCode: ClaudeScreenFailureCode.ProcessExited);
        ClaudeCliActiveSource source = CreateSource(session: session, startupTimeout: TimeSpan.FromSeconds(3));

        ClaudeUsageObservation observation = await source.RefreshAsync(CancellationToken.None);

        Assert.Equal(UsageAvailability.Unavailable, observation.Availability);
        Assert.Equal("PROCESS_EXITED", observation.Reason);
    }

    [Fact]
    public async Task RefreshAsyncMapsNotInstalledExecutable()
    {
        ClaudeCliActiveSource source = CreateSource(
            resolveExecutable: _ => new ClaudeExecutableInfo("", null, false, null, "CLAUDE_NOT_INSTALLED"));

        ClaudeUsageObservation observation = await source.RefreshAsync(CancellationToken.None);

        Assert.Equal(UsageAvailability.NotInstalled, observation.Availability);
        Assert.Equal("CLAUDE_NOT_INSTALLED", observation.Reason);
    }

    [Fact]
    public async Task RefreshAsyncMapsUntrustedExecutableToUnsupported()
    {
        ClaudeCliActiveSource source = CreateSource(
            resolveExecutable: _ => new ClaudeExecutableInfo("claude.exe", null, false, null, "UNTRUSTED_EXECUTABLE"));

        ClaudeUsageObservation observation = await source.RefreshAsync(CancellationToken.None);

        Assert.Equal(UsageAvailability.Unsupported, observation.Availability);
        Assert.Equal("UNTRUSTED_EXECUTABLE", observation.Reason);
    }

    [Fact]
    public async Task RefreshAsyncMapsUnsupportedCapabilities()
    {
        ClaudeCliActiveSource source = CreateSource(
            probeCapabilities: (_, _, _) =>
                Task.FromResult(new ClaudeCliCapabilities(false, "2.1.218", "REQUIRED_FLAG_MISSING")));

        ClaudeUsageObservation observation = await source.RefreshAsync(CancellationToken.None);

        Assert.Equal(UsageAvailability.Unsupported, observation.Availability);
        Assert.Equal("REQUIRED_FLAG_MISSING", observation.Reason);
    }

    [Fact]
    public async Task RefreshAsyncMapsWorkspaceProvisionFailure()
    {
        var workspace = new FakeClaudeWorkspaceProvisioner(ensureWorkspace: () => throw new IOException("disk full"));
        ClaudeCliActiveSource source = CreateSource(workspace: workspace);

        ClaudeUsageObservation observation = await source.RefreshAsync(CancellationToken.None);

        Assert.Equal(UsageAvailability.Error, observation.Availability);
        Assert.Equal("WORKSPACE_PROVISION_FAILED", observation.Reason);
    }

    [Fact]
    public async Task RefreshAsyncMapsSessionStartFailure()
    {
        var factory = new FakeClaudeScreenSessionFactory(
            () => ScreenSessionResult<IClaudeScreenSession>.Fail(ClaudeScreenFailureCode.ProcessStartFailed));
        ClaudeCliActiveSource source = CreateSource(sessionFactory: factory);

        ClaudeUsageObservation observation = await source.RefreshAsync(CancellationToken.None);

        Assert.Equal(UsageAvailability.Error, observation.Availability);
        Assert.Equal("PROCESS_START_FAILED", observation.Reason);
    }

    [Fact]
    public async Task RefreshAsyncReturnsConsoleBufferReadFailedWhenReadKeepsFailing()
    {
        var session = new FakeClaudeScreenSession(failureCode: ClaudeScreenFailureCode.ScreenReadFailed);
        ClaudeCliActiveSource source = CreateSource(session: session, startupTimeout: TimeSpan.FromMilliseconds(400));

        ClaudeUsageObservation observation = await source.RefreshAsync(CancellationToken.None);

        Assert.Equal(UsageAvailability.Unavailable, observation.Availability);
        Assert.Equal("CONSOLE_BUFFER_READ_FAILED", observation.Reason);
    }

    [Fact]
    public async Task WorkspacePathExposesTheProvisionerValue()
    {
        var workspace = new FakeClaudeWorkspaceProvisioner("C:\\custom-workspace");
        ClaudeCliActiveSource source = CreateSource(workspace: workspace);

        Assert.Equal("C:\\custom-workspace", source.WorkspacePath);
    }

    private static ScreenSnapshot ScreenOf(string[] lines) => new(lines, 80, lines.Length);

    private static ClaudeCliActiveSource CreateSource(
        FakeClaudeWorkspaceProvisioner? workspace = null,
        FakeClaudeScreenSessionFactory? sessionFactory = null,
        FakeClaudeScreenSession? session = null,
        Func<string?, ClaudeExecutableInfo>? resolveExecutable = null,
        Func<string, TimeSpan, CancellationToken, Task<ClaudeCliCapabilities>>? probeCapabilities = null,
        TimeSpan? startupTimeout = null)
    {
        FakeClaudeScreenSessionFactory factory = sessionFactory
            ?? new FakeClaudeScreenSessionFactory(() => ScreenSessionResult<IClaudeScreenSession>.Ok(session ?? new FakeClaudeScreenSession(ScreenOf(ReadyScreen))));
        return new ClaudeCliActiveSource(
            null,
            "claude-statusline-bridge.ps1",
            startupTimeout ?? TimeSpan.FromSeconds(5),
            workspace ?? new FakeClaudeWorkspaceProvisioner(),
            factory,
            new FakeClaudeExecutableLocator(resolveExecutable ?? (_ => new ClaudeExecutableInfo("claude.exe", "2.1.218", true, "Anthropic, PBC", null))),
            probeCapabilities ?? ((_, _, _) => Task.FromResult(new ClaudeCliCapabilities(true, "2.1.218", null))));
    }

    private sealed class FakeClaudeWorkspaceProvisioner : IClaudeWorkspaceProvisioner
    {
        private readonly Func<string>? _ensureWorkspace;

        public FakeClaudeWorkspaceProvisioner(string workspacePath = "C:\\fake-workspace", Func<string>? ensureWorkspace = null)
        {
            WorkspacePath = workspacePath;
            _ensureWorkspace = ensureWorkspace;
        }

        public string WorkspacePath { get; }

        public string EnsureWorkspace() => _ensureWorkspace?.Invoke() ?? WorkspacePath;
    }

    private sealed class FakeClaudeExecutableLocator(Func<string?, ClaudeExecutableInfo> resolve) : IClaudeExecutableLocator
    {
        public ClaudeExecutableInfo Resolve(string? configuredPath) => resolve(configuredPath);
    }

    private sealed class FakeClaudeScreenSessionFactory(Func<ScreenSessionResult<IClaudeScreenSession>> start) : IClaudeScreenSessionFactory
    {
        public Task<ScreenSessionResult<IClaudeScreenSession>> StartAsync(
            string executablePath,
            string workspacePath,
            CancellationToken cancellationToken) => Task.FromResult(start());
    }

    private sealed class FakeClaudeScreenSession : IClaudeScreenSession
    {
        private readonly List<ScreenSnapshot> _screens;
        private readonly ClaudeScreenFailureCode? _failureCode;
        private int _readIndex;

        public FakeClaudeScreenSession(params ScreenSnapshot[] screens)
        {
            _screens = [.. screens];
        }

        public FakeClaudeScreenSession(ClaudeScreenFailureCode failureCode)
        {
            _screens = [];
            _failureCode = failureCode;
        }

        public List<(string Command, TimeSpan Timeout)> Calls { get; } = [];

        public Task<ScreenSessionResult<ScreenSnapshot>> ReadScreenAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (_failureCode is { } code)
                return Task.FromResult(ScreenSessionResult<ScreenSnapshot>.Fail(code));
            ScreenSnapshot snapshot = _screens[Math.Min(_readIndex, _screens.Count - 1)];
            _readIndex++;
            return Task.FromResult(ScreenSessionResult<ScreenSnapshot>.Ok(snapshot));
        }

        public Task<ScreenSessionResult> SendUsageCommandAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add(("usage", timeout));
            return Task.FromResult(ScreenSessionResult.Ok());
        }

        public Task<ScreenSessionResult> SendEscapeAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add(("escape", timeout));
            return Task.FromResult(ScreenSessionResult.Ok());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
