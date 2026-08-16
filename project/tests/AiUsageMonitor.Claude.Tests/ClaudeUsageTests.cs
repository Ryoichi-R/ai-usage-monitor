using AiUsageMonitor.Claude.StatusLine;
using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Claude.Usage;
using AiUsageMonitor.Claude.Transport;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;
using System.IO.Pipes;
using System.Text;

namespace AiUsageMonitor.Claude.Tests;

public sealed class ClaudeUsageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);

    [Fact]
    public void ParserExtractsOnlySupportedWindows()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new
        {
            version = "2.1.118",
            cwd = "secret",
            rate_limits = new
            {
                five_hour = new { used_percentage = 25, resets_at = Now.AddHours(1).ToUnixTimeSeconds() },
                seven_day = new { used_percentage = 40, resets_at = Now.AddDays(1).ToUnixTimeSeconds() },
            }
        });
        UsageSnapshot result = ClaudeStatusLineParser.Parse(json, Now);
        Assert.Equal(UsageAvailability.Available, result.Availability);
        Assert.Equal([300, 10080], result.Windows.Select(window => window.WindowDurationMins));
        Assert.Equal(75, result.Windows[0].RemainingPercent);
    }

    [Theory]
    [InlineData("{", "MALFORMED_JSON")]
    [InlineData("[]", "INVALID_ROOT")]
    [InlineData("\"value\"", "INVALID_ROOT")]
    [InlineData("""{"protocol":2}""", "PROTOCOL_MISMATCH")]
    [InlineData("""{"rate_limits":{"five_hour":{"used_percentage":"1"}}}""", "INVALID_PERCENTAGE")]
    [InlineData("""{"rate_limits":{"five_hour":{"used_percentage":-1}}}""", "INVALID_PERCENTAGE")]
    [InlineData("""{"rate_limits":{"five_hour":{"used_percentage":101}}}""", "INVALID_PERCENTAGE")]
    public void ParserRejectsInvalidPayload(string json, string reason)
    {
        UsageSnapshot result = ClaudeStatusLineParser.Parse(json, Now);
        Assert.Equal(UsageAvailability.Error, result.Availability);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void MissingLimitsIsWaitingEvenForOldVersion()
    {
        UsageSnapshot result = ClaudeStatusLineParser.Parse("""{"version":"2.1.1"}""", Now);
        Assert.Equal(UsageAvailability.Waiting, result.Availability);
    }

    [Fact]
    public void NewerObservationWinsEvenWhenUsedPercentDrops()
    {
        // 期間途中の上限引上げ等で使用済み割合は低下し得る。monotonic max前提を残さない。
        var merger = new ClaudeUsageObservationMerger();
        merger.Merge(Snapshot(80, Now.AddHours(1), Now));
        UsageSnapshot same = merger.Merge(Snapshot(20, Now.AddHours(1).AddMinutes(1), Now.AddSeconds(1)));
        Assert.Equal(20, same.Windows.Single().UsedPercent);
    }

    [Fact]
    public void LateArrivingOlderObservationDoesNotOverwriteNewerOne()
    {
        var merger = new ClaudeUsageObservationMerger();
        merger.Merge(Snapshot(40, Now.AddHours(1), Now.AddSeconds(10)));
        UsageSnapshot late = merger.Merge(Snapshot(90, Now.AddHours(1), Now.AddSeconds(1)));
        Assert.Equal(40, late.Windows.Single().UsedPercent);
    }

    [Fact]
    public void LateArrivingOlderFailureDoesNotInvalidateNewerSuccess()
    {
        var merger = new ClaudeUsageObservationMerger();
        merger.Merge(Snapshot(40, Now.AddHours(1), Now.AddSeconds(10)));
        UsageSnapshot failure = UsageSnapshot.Loading(
            UsageProvider.Claude,
            Now.AddSeconds(1)) with
        {
            Availability = UsageAvailability.Setup,
            Reason = "CLAUDE_TRUST_REQUIRED",
        };

        UsageSnapshot result = merger.Merge(failure);

        Assert.Equal(UsageAvailability.Available, result.Availability);
        Assert.Equal(40, result.Windows.Single().UsedPercent);
    }

    [Fact]
    public void RolloverToNewWindowIsAccepted()
    {
        var merger = new ClaudeUsageObservationMerger();
        merger.Merge(Snapshot(80, Now.AddHours(1), Now));
        UsageSnapshot next = merger.Merge(Snapshot(10, Now.AddHours(3), Now.AddSeconds(2)));
        Assert.Equal(10, next.Windows.Single().UsedPercent);
        Assert.Equal(Now.AddHours(3), next.Windows.Single().ResetsAt);
    }

    [Fact]
    public void WindowFromAnEarlierResetIsRejected()
    {
        var merger = new ClaudeUsageObservationMerger();
        merger.Merge(Snapshot(40, Now.AddHours(3), Now));
        UsageSnapshot stale = merger.Merge(Snapshot(90, Now.AddHours(1), Now.AddSeconds(1)));
        Assert.Equal(40, stale.Windows.Single().UsedPercent);
        Assert.Equal(Now.AddHours(3), stale.Windows.Single().ResetsAt);
    }

    [Fact]
    public void RejectedEarlierResetDoesNotAdvanceFreshness()
    {
        var merger = new ClaudeUsageObservationMerger();
        merger.Merge(Snapshot(40, Now.AddHours(3), Now));
        UsageSnapshot result = merger.Merge(
            Snapshot(90, Now.AddMinutes(30), Now.AddSeconds(1)));
        Assert.Equal(Now, result.TakenAt);
        Assert.Equal(Now, result.ReceivedAt);
        Assert.Equal(Now, result.LastSuccessfulAt);
    }

    [Fact]
    public void InvalidFutureCurrentRecoversToValidIncoming()
    {
        var merger = new ClaudeUsageObservationMerger();
        SeedLegacyCurrent(
            merger,
            Snapshot(57, Now.AddDays(1), Now));
        UsageSnapshot recovered = merger.Merge(
            Snapshot(10, Now.AddHours(4), Now.AddSeconds(1)));
        Assert.Equal(10, recovered.Windows.Single().UsedPercent);
        Assert.Equal(Now.AddHours(4), recovered.Windows.Single().ResetsAt);
    }

    [Fact]
    public void InvalidFirstIncomingIsRejectedWithoutCreatingFreshHistory()
    {
        var merger = new ClaudeUsageObservationMerger();

        UsageSnapshot result = merger.Merge(
            Snapshot(57, Now.AddDays(1), Now));

        Assert.Equal(UsageAvailability.Error, result.Availability);
        Assert.Equal("USAGE_RESET_OUT_OF_RANGE", result.Reason);
        Assert.Empty(result.Windows);
        Assert.Null(result.LastSuccessfulAt);
    }

    [Fact]
    public void InvalidSevenDayLegacyCurrentRecoversToValidIncoming()
    {
        var merger = new ClaudeUsageObservationMerger();
        SeedLegacyCurrent(
            merger,
            Snapshot(57, Now.AddYears(1), Now) with
            {
                Windows =
                [
                    new(
                        null,
                        null,
                        "seven_day",
                        57,
                        UsageWindowPolicy.SevenDayDurationMinutes,
                        Now.AddYears(1),
                        null),
                ],
            });
        UsageSnapshot incoming = Snapshot(10, Now.AddDays(4), Now.AddSeconds(1)) with
        {
            Windows =
            [
                new(
                    null,
                    null,
                    "seven_day",
                    10,
                    UsageWindowPolicy.SevenDayDurationMinutes,
                    Now.AddDays(4),
                    null),
            ],
        };

        UsageSnapshot recovered = merger.Merge(incoming);

        Assert.Equal(10, recovered.Windows.Single().UsedPercent);
        Assert.Equal(Now.AddDays(4), recovered.Windows.Single().ResetsAt);
    }

    [Fact]
    public void InvalidIncomingDoesNotAdvanceFreshness()
    {
        var merger = new ClaudeUsageObservationMerger();
        merger.Merge(Snapshot(40, Now.AddHours(3), Now));
        UsageSnapshot result = merger.Merge(
            Snapshot(90, Now.AddDays(1), Now.AddSeconds(1)));
        Assert.Equal(40, result.Windows.Single().UsedPercent);
        Assert.Equal(Now, result.ReceivedAt);
        Assert.Equal("USAGE_RESET_OUT_OF_RANGE", result.Reason);
        Assert.Equal(Now, result.LastSuccessfulAt);
    }

    [Fact]
    public void PartialWindowRejectionDoesNotAdvanceSnapshotFreshness()
    {
        var merger = new ClaudeUsageObservationMerger();
        UsageSnapshot current = Snapshot(20, Now.AddHours(3), Now) with
        {
            Windows =
            [
                new(null, null, "five_hour", 20, 300, Now.AddHours(3), null),
                new(null, null, "seven_day", 30, 10080, Now.AddDays(3), null),
            ],
        };
        merger.Merge(current);
        UsageSnapshot partial = current with
        {
            TakenAt = Now.AddMinutes(1),
            ReceivedAt = Now.AddMinutes(1),
            Windows =
            [
                new(null, null, "five_hour", 10, 300, Now.AddHours(4), null),
                new(null, null, "seven_day", 10, 10080, Now.AddDays(8), null),
            ],
        };

        UsageSnapshot result = merger.Merge(partial);

        Assert.Equal(Now, result.TakenAt);
        Assert.Equal(Now, result.ReceivedAt);
        Assert.Equal(Now, result.LastSuccessfulAt);
        Assert.Equal([20d, 30d], result.Windows.Select(window => window.UsedPercent));
        Assert.Equal("USAGE_RESET_OUT_OF_RANGE", result.Reason);
    }

    [Fact]
    public void KnownResetIsCarriedOverWhenNewObservationOmitsIt()
    {
        var merger = new ClaudeUsageObservationMerger();
        merger.Merge(Snapshot(40, Now.AddHours(1), Now));
        UsageSnapshot merged = merger.Merge(Snapshot(55, null, Now.AddSeconds(1)));
        Assert.Equal(55, merged.Windows.Single().UsedPercent);
        Assert.Equal(Now.AddHours(1), merged.Windows.Single().ResetsAt);
    }

    [Fact]
    public void CurrentBecomesStaleAfterTimeoutOrReset()
    {
        var merger = new ClaudeUsageObservationMerger();
        merger.Merge(Snapshot(50, Now.AddMinutes(5), Now));
        Assert.Equal(UsageAvailability.Stale, merger.Current(Now.AddMinutes(11)).Availability);
    }

    [Fact]
    public void FailedRefreshDoesNotExtendSuccessfulObservationTtl()
    {
        var merger = new ClaudeUsageObservationMerger();
        merger.Merge(Snapshot(50, Now.AddHours(1), Now));

        UsageSnapshot failure = UsageSnapshot.Loading(UsageProvider.Claude, Now.AddMinutes(5)) with
        {
            Availability = UsageAvailability.Unavailable,
            Reason = "TIMEOUT",
        };
        UsageSnapshot withinTtl = merger.Merge(failure);
        Assert.Equal(UsageAvailability.Available, withinTtl.Availability);
        Assert.Equal(Now, withinTtl.ReceivedAt);

        UsageSnapshot laterFailure = failure with { TakenAt = Now.AddMinutes(11), ReceivedAt = Now.AddMinutes(11) };
        UsageSnapshot expired = merger.Merge(laterFailure);
        Assert.Equal(UsageAvailability.Stale, expired.Availability);
        Assert.Equal(Now, expired.ReceivedAt);
    }

    [Theory]
    [InlineData(UsageAvailability.SignedOut, "CLAUDE_SIGNED_OUT")]
    [InlineData(UsageAvailability.Setup, "CLAUDE_TRUST_REQUIRED")]
    [InlineData(UsageAvailability.NotInstalled, "CLAUDE_NOT_INSTALLED")]
    public void ActionableFailureReasonSurvivesStaleTimeout(
        UsageAvailability availability,
        string reason)
    {
        // 2026-08-15インシデントの根本原因: staleAfter経過後、Currentが理由を機械的に
        // RECEIVE_TIMEOUTへ丸めていたため、SignedOut等の実行可能な理由が消えていた。
        var merger = new ClaudeUsageObservationMerger();
        UsageSnapshot failure = UsageSnapshot.Loading(UsageProvider.Claude, Now) with
        {
            Availability = availability,
            Reason = reason,
        };
        merger.Merge(failure);

        UsageSnapshot stale = merger.Current(Now.AddMinutes(11));

        Assert.Equal(UsageAvailability.Stale, stale.Availability);
        Assert.Equal(reason, stale.Reason);
        Assert.True(stale.IsStale);
    }

    [Fact]
    public void NonActionableFailureReasonBecomesReceiveTimeoutAfterStale()
    {
        var merger = new ClaudeUsageObservationMerger();
        UsageSnapshot failure = UsageSnapshot.Loading(UsageProvider.Claude, Now) with
        {
            Availability = UsageAvailability.Unavailable,
            Reason = "RPC_TIMEOUT",
        };
        merger.Merge(failure);

        UsageSnapshot stale = merger.Current(Now.AddMinutes(11));

        Assert.Equal(UsageAvailability.Stale, stale.Availability);
        Assert.Equal("RECEIVE_TIMEOUT", stale.Reason);
    }

    [Fact]
    public void SignedOutThenUnsupportedDoesNotInventSuccessfulHistory()
    {
        var merger = new ClaudeUsageObservationMerger();
        UsageSnapshot signedOut = UsageSnapshot.Loading(
            UsageProvider.Claude,
            Now) with
        {
            Availability = UsageAvailability.SignedOut,
            Reason = "CLAUDE_SIGNED_OUT",
            LastSuccessfulAt = Now.AddDays(-1),
        };
        UsageSnapshot unsupported = signedOut with
        {
            TakenAt = Now.AddMinutes(1),
            ReceivedAt = Now.AddMinutes(1),
            Availability = UsageAvailability.Unsupported,
            Reason = "REQUIRED_FLAG_MISSING",
        };

        merger.Merge(signedOut);
        UsageSnapshot result = merger.Merge(unsupported);

        Assert.Equal(UsageAvailability.Unsupported, result.Availability);
        Assert.Empty(result.Windows);
        Assert.Null(result.LastSuccessfulAt);
    }

    [Fact]
    public void UnsupportedAfterSuccessRetainsOnlySuccessfulTimestamp()
    {
        var merger = new ClaudeUsageObservationMerger();
        merger.Merge(Snapshot(50, Now.AddHours(1), Now));
        UsageSnapshot unsupported = UsageSnapshot.Loading(
            UsageProvider.Claude,
            Now.AddMinutes(1)) with
        {
            Availability = UsageAvailability.Unsupported,
            Reason = "REQUIRED_FLAG_MISSING",
        };

        UsageSnapshot result = merger.Merge(unsupported);

        Assert.Equal(UsageAvailability.Unsupported, result.Availability);
        Assert.Empty(result.Windows);
        Assert.Equal(Now, result.LastSuccessfulAt);
    }

    [Fact]
    public void MissingWindowIsNotCarriedFromEarlierObservation()
    {
        var merger = new ClaudeUsageObservationMerger();
        UsageSnapshot both = Snapshot(20, Now.AddHours(1), Now) with
        {
            Windows =
            [
                new(null, null, "five_hour", 20, 300, Now.AddHours(1), null),
                new(null, null, "seven_day", 30, 10080, Now.AddDays(1), null),
            ],
        };
        merger.Merge(both);

        UsageSnapshot one = merger.Merge(Snapshot(25, Now.AddHours(1), Now.AddSeconds(1)));
        Assert.Single(one.Windows);
        Assert.Equal(300, one.Windows[0].WindowDurationMins);
    }

    [Fact]
    public async Task PipeServerReceivesValidatedMinimalPayload()
    {
        string pipeName = CreateTestPipeName();
        await using var server = new ClaudeUsagePipeServer(pipeName);
        var completion = new TaskCompletionSource<UsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ObservationReceived += snapshot => completion.TrySetResult(snapshot);
        server.Start();
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(timeout.Token);
        byte[] payload = Encoding.UTF8.GetBytes("""{"protocol":1,"version":"2.1.118","rate_limits":null}""");
        await client.WriteAsync(payload, timeout.Token);
        await client.FlushAsync(timeout.Token);
        client.Dispose();
        UsageSnapshot received = await completion.Task.WaitAsync(timeout.Token);
        Assert.Equal(UsageAvailability.Waiting, received.Availability);
        Assert.Equal("2.1.118", received.ClientVersion);
    }

    [Fact]
    public async Task OversizedPayloadProducesOneObservationAndListenerRecovers()
    {
        string pipeName = CreateTestPipeName();
        await using var server = new ClaudeUsagePipeServer(pipeName);
        var observations = new List<UsageSnapshot>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ObservationReceived += snapshot =>
        {
            lock (observations)
            {
                observations.Add(snapshot);
                if (observations.Count == 2)
                    completion.TrySetResult();
            }
        };
        server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await SendAsync(pipeName, new byte[ClaudeStatusLineParser.MaximumPayloadBytes + 1], timeout.Token);
        await SendAsync(pipeName, Encoding.UTF8.GetBytes("""{"protocol":1,"rate_limits":null}"""), timeout.Token);
        await completion.Task.WaitAsync(timeout.Token);

        Assert.Equal(["PAYLOAD_TOO_LARGE", "RATE_LIMITS_MISSING"], observations.Select(x => x.Reason));
    }

    private static async Task SendAsync(string pipeName, byte[] payload, CancellationToken cancellationToken)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(cancellationToken);
        await client.WriteAsync(payload, cancellationToken);
        await client.FlushAsync(cancellationToken);
    }

    private static string CreateTestPipeName() =>
        $"{ClaudeUsagePipeServer.PipeName}-test-{Guid.NewGuid():N}";

    private static UsageSnapshot Snapshot(double used, DateTimeOffset? reset, DateTimeOffset received) =>
        new(UsageProvider.Claude, received, received, UsageAvailability.Available, null, null,
            [new(null, null, "five_hour", used, 300, reset, null)], null, false, received, "2.1.118");

    private static void SeedLegacyCurrent(
        ClaudeUsageObservationMerger merger,
        UsageSnapshot snapshot)
    {
        System.Reflection.FieldInfo field = typeof(ClaudeUsageObservationMerger)
            .GetField(
                "_current",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Merger current field was not found.");
        field.SetValue(merger, snapshot);
    }

    [Fact]
    public async Task AutomaticPeriodicInvokesActiveEvenWhenPassiveIsFresh()
    {
        var source = new FakeSource(Snapshot(10, Now.AddHours(1), Now));
        var coordinator = new ClaudeUsageSourceCoordinator(source);

        ClaudeActiveRefreshOutcome result = await coordinator.RefreshAsync(
            ClaudeUsageAcquisitionMode.Automatic,
            false,
            Now.AddMinutes(1),
            CancellationToken.None);

        Assert.True(result.WasInvoked);
        Assert.Equal(10, result.Observation!.Snapshot.Windows.Single().UsedPercent);
        Assert.Equal(1, source.CallCount);
    }

    [Fact]
    public async Task OfficialCliModeInvokesActiveSource()
    {
        var source = new FakeSource(Snapshot(15, Now.AddHours(1), Now));
        var coordinator = new ClaudeUsageSourceCoordinator(source);

        ClaudeActiveRefreshOutcome result = await coordinator.RefreshAsync(
            ClaudeUsageAcquisitionMode.OfficialCliOnly,
            false,
            Now,
            CancellationToken.None);

        Assert.True(result.WasInvoked);
        Assert.Equal(15, result.Observation!.Snapshot.Windows.Single().UsedPercent);
        Assert.Equal(1, source.CallCount);
    }

    [Fact]
    public async Task ConcurrentActiveRefreshesShareOneInvocation()
    {
        var completion = new TaskCompletionSource<UsageSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource(completion.Task);
        var coordinator = new ClaudeUsageSourceCoordinator(source);

        Task<ClaudeActiveRefreshOutcome> first = coordinator.RefreshAsync(
            ClaudeUsageAcquisitionMode.OfficialCliOnly, true, Now, CancellationToken.None);
        Task<ClaudeActiveRefreshOutcome> second = coordinator.RefreshAsync(
            ClaudeUsageAcquisitionMode.OfficialCliOnly, true, Now, CancellationToken.None);
        completion.SetResult(Snapshot(25, Now.AddHours(1), Now));

        await Task.WhenAll(first, second);
        Assert.Equal(1, source.CallCount);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotCancelSharedActiveInvocation()
    {
        var completion = new TaskCompletionSource<UsageSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeSource(completion.Task);
        var coordinator = new ClaudeUsageSourceCoordinator(source);
        using var waiterCancellation = new CancellationTokenSource();

        Task<ClaudeActiveRefreshOutcome> cancelledWaiter = coordinator.RefreshAsync(
            ClaudeUsageAcquisitionMode.Automatic,
            true,
            Now,
            waiterCancellation.Token);
        Task<ClaudeActiveRefreshOutcome> survivingWaiter = coordinator.RefreshAsync(
            ClaudeUsageAcquisitionMode.Automatic,
            true,
            Now,
            CancellationToken.None);
        waiterCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await cancelledWaiter);

        completion.SetResult(Snapshot(26, Now.AddHours(1), Now));
        ClaudeActiveRefreshOutcome result = await survivingWaiter;

        Assert.True(result.WasInvoked);
        Assert.Equal(26, result.Observation!.Snapshot.Windows.Single().UsedPercent);
        Assert.Equal(1, source.CallCount);
    }

    [Fact]
    public async Task AutomaticFailureBackoffSkipsRepeatedInvocationButManualBypassesIt()
    {
        UsageSnapshot failure = UsageSnapshot.Loading(UsageProvider.Claude, Now) with
        {
            Availability = UsageAvailability.Unavailable,
            Reason = "TIMEOUT",
        };
        var source = new FakeSource(failure);
        var coordinator = new ClaudeUsageSourceCoordinator(source);

        await coordinator.RefreshAsync(
            ClaudeUsageAcquisitionMode.Automatic, false, Now, CancellationToken.None);
        await coordinator.RefreshAsync(
            ClaudeUsageAcquisitionMode.Automatic, false, Now.AddSeconds(10), CancellationToken.None);
        await coordinator.RefreshAsync(
            ClaudeUsageAcquisitionMode.Automatic, true, Now.AddSeconds(20), CancellationToken.None);

        Assert.Equal(2, source.CallCount);
    }

    [Fact]
    public void ObservationValidatesProviderAndExposesRedactedMetadata()
    {
        UsageSnapshot snapshot = Snapshot(35, Now.AddHours(1), Now) with
        {
            Reason = "TEST_REASON",
        };
        var observation = new ClaudeUsageObservation(
            ClaudeUsageSourceKind.StatusLineActive,
            snapshot);

        Assert.Equal(Now, observation.ObservedAt);
        Assert.Equal(Now, observation.ReceivedAt);
        Assert.Equal(UsageAvailability.Available, observation.Availability);
        Assert.Equal("TEST_REASON", observation.Reason);
        Assert.Equal("2.1.118", observation.ClientVersion);
        Assert.Contains("監視アプリ起動セッション", observation.DisplayName);
        Assert.Throws<ArgumentNullException>(() =>
            new ClaudeUsageObservation(ClaudeUsageSourceKind.StatusLinePassive, null!));
        Assert.Throws<ArgumentException>(() =>
            new ClaudeUsageObservation(
                ClaudeUsageSourceKind.StatusLinePassive,
                UsageSnapshot.Loading(UsageProvider.Codex, Now)));
    }

    [Theory]
    [InlineData(ClaudeUsageSourceKind.StatusLinePassive, "常駐セッション")]
    [InlineData(ClaudeUsageSourceKind.StatusLineActive, "監視アプリ起動セッション")]
    [InlineData(ClaudeUsageSourceKind.CliScreen, "/usage")]
    public void ObservationDisplayNameIdentifiesSource(
        ClaudeUsageSourceKind source,
        string expected)
    {
        var observation = new ClaudeUsageObservation(
            source,
            Snapshot(10, Now.AddHours(1), Now));

        Assert.Contains(expected, observation.DisplayName);
    }

    [Fact]
    public async Task StatusLineOnlyNeverStartsActiveSource()
    {
        var source = new FakeSource(Snapshot(10, Now.AddHours(1), Now));
        var coordinator = new ClaudeUsageSourceCoordinator(source);

        ClaudeActiveRefreshOutcome result = await coordinator.RefreshAsync(
            ClaudeUsageAcquisitionMode.StatusLineOnly,
            false,
            Now,
            CancellationToken.None);

        Assert.False(result.WasInvoked);
        Assert.Equal("STATUSLINE_ONLY", result.SkipReason);
        Assert.Equal(0, source.CallCount);
    }

    [Fact]
    public async Task ActiveSourceExceptionIsMappedToOutcome()
    {
        var source = new FakeSource(() => throw new InvalidOperationException("test"));
        var coordinator = new ClaudeUsageSourceCoordinator(source);
        ClaudeActiveRefreshOutcome result = await coordinator.RefreshAsync(
            ClaudeUsageAcquisitionMode.OfficialCliOnly,
            true,
            Now,
            CancellationToken.None);

        Assert.True(result.WasInvoked);
        Assert.Equal(UsageAvailability.Error, result.Observation!.Availability);
        Assert.Equal("ACTIVE_SOURCE_EXCEPTION", result.Observation.Reason);
    }

    [Fact]
    public async Task SuccessfulManualRefreshClearsBackoff()
    {
        var source = new FakeSource(Snapshot(45, Now.AddHours(1), Now));
        var coordinator = new ClaudeUsageSourceCoordinator(source);
        ClaudeActiveRefreshOutcome result = await coordinator.RefreshAsync(
            ClaudeUsageAcquisitionMode.OfficialCliOnly,
            true,
            Now,
            CancellationToken.None);

        Assert.Equal(UsageAvailability.Available, result.Observation!.Availability);
    }

    private sealed class FakeSource : IClaudeUsageSource
    {
        private readonly Func<Task<UsageSnapshot>> _next;

        public FakeSource(UsageSnapshot snapshot) : this(Task.FromResult(snapshot)) { }

        public FakeSource(Task<UsageSnapshot> snapshot) => _next = () => snapshot;

        public FakeSource(Func<Task<UsageSnapshot>> next) => _next = next;

        public int CallCount { get; private set; }

        public async Task<ClaudeUsageObservation> RefreshAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            UsageSnapshot snapshot = await _next().WaitAsync(cancellationToken);
            return new(ClaudeUsageSourceKind.StatusLineActive, snapshot);
        }
    }
}
