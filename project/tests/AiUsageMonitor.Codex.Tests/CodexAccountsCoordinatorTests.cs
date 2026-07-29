using System.Collections.Concurrent;
using AiUsageMonitor.Codex.Client;
using AiUsageMonitor.Codex.Runtime;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Codex.Tests;

public sealed class CodexAccountsCoordinatorTests
{
    [Fact]
    public async Task ConfiguresRefreshesAndReusesTwoIsolatedAccounts()
    {
        string root = CreateRoot();
        try
        {
            string accountA = Directory.CreateDirectory(Path.Combine(root, "account-a")).FullName;
            string accountB = Directory.CreateDirectory(Path.Combine(root, "account-b")).FullName;
            int clientsCreated = 0;
            await using var coordinator = new CodexAccountsCoordinator(
                null,
                root,
                configuration =>
                {
                    Interlocked.Increment(ref clientsCreated);
                    return new(configuration);
                });
            var snapshots = new ConcurrentDictionary<string, CodexAccountSnapshot>();
            coordinator.SnapshotChanged += snapshot => snapshots[snapshot.AccountId] = snapshot;
            AppSettings settings = Settings(accountA, accountB);

            await coordinator.ConfigureAsync(settings, null);
            await coordinator.RefreshAsync();

            Assert.Equal(2, clientsCreated);
            Assert.Equal(25, snapshots["a"].Snapshot.Windows[0].UsedPercent);
            Assert.Equal(70, snapshots["b"].Snapshot.Windows[0].UsedPercent);
            Assert.Equal(11.25m, snapshots["a"].Snapshot.CreditSnapshot?.Balance);
            Assert.Equal(22.50m, snapshots["b"].Snapshot.CreditSnapshot?.Balance);

            await coordinator.ConfigureAsync(settings with
            {
                CodexAccounts =
                [
                    settings.CodexAccounts[0] with { DisplayName = "Renamed" },
                    settings.CodexAccounts[1] with { ShowInWidget = false },
                ],
            }, null);
            await coordinator.RefreshAsync();
            Assert.Equal(2, clientsCreated);
            Assert.Equal("Renamed", snapshots["a"].DisplayName);
            Assert.False(snapshots["b"].ShowInWidget);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task InvalidAndDuplicateHomesDoNotStartClients()
    {
        string root = CreateRoot();
        try
        {
            string home = Directory.CreateDirectory(Path.Combine(root, "account-a")).FullName;
            int clientsCreated = 0;
            await using var coordinator = new CodexAccountsCoordinator(
                null,
                root,
                configuration =>
                {
                    Interlocked.Increment(ref clientsCreated);
                    return new(configuration);
                });
            var snapshots = new ConcurrentDictionary<string, CodexAccountSnapshot>();
            coordinator.SnapshotChanged += snapshot => snapshots[snapshot.AccountId] = snapshot;
            await coordinator.ConfigureAsync(new()
            {
                CodexExecutablePath = FindFakeServer(),
                CodexAccounts =
                [
                    new() { Id = "a", DisplayName = "A", CodexHomePath = home },
                    new() { Id = "duplicate", DisplayName = "Duplicate", CodexHomePath = home },
                    new() { Id = "missing", DisplayName = "Missing", CodexHomePath = Path.Combine(root, "missing") },
                ],
            }, null);
            Assert.Equal(1, clientsCreated);
            Assert.Equal("CODEX_HOME_DUPLICATE", snapshots["duplicate"].Snapshot.Reason);
            Assert.Equal("CODEX_HOME_UNAVAILABLE", snapshots["missing"].Snapshot.Reason);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(32)]
    public void StaggerIsOrderedAndBounded(int count)
    {
        TimeSpan[] offsets = Enumerable.Range(0, count)
            .Select(index => CodexAccountsCoordinator.GetInitialStagger(index, count))
            .ToArray();
        Assert.Equal(TimeSpan.Zero, offsets[0]);
        Assert.True(offsets[^1] <= TimeSpan.FromSeconds(10));
        Assert.True(offsets.Zip(offsets.Skip(1), (left, right) => right - left)
            .All(gap => gap >= TimeSpan.Zero && gap <= TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(0d, 90)]
    [InlineData(0.5d, 100)]
    [InlineData(1d, 110)]
    [InlineData(-1d, 90)]
    [InlineData(2d, 110)]
    public void PollingIntervalUsesBoundedIndependentJitter(double sample, int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            CodexAccountsCoordinator.GetPollingInterval(
                TimeSpan.FromSeconds(100),
                sample));
    }

    [Fact]
    public async Task PeriodicSchedulerRefreshesUntilCancellation()
    {
        string root = CreateRoot();
        try
        {
            string account = Directory.CreateDirectory(Path.Combine(root, "account-a")).FullName;
            int jitterSamples = 0;
            await using var coordinator = new CodexAccountsCoordinator(
                null,
                root,
                pollingJitterFactory: () =>
                {
                    Interlocked.Increment(ref jitterSamples);
                    return 0.5d;
                },
                notificationCooldown: TimeSpan.FromHours(1));
            int snapshots = 0;
            var secondSnapshot = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.SnapshotChanged += _ =>
            {
                if (Interlocked.Increment(ref snapshots) >= 2)
                    secondSnapshot.TrySetResult();
            };
            await coordinator.ConfigureAsync(SingleSettings(account), null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            Task polling = coordinator.RunPeriodicPollingAsync(
                TimeSpan.FromMilliseconds(30),
                cancellation.Token);
            await secondSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => polling);

            Assert.True(Volatile.Read(ref jitterSamples) >= 1);
            Assert.True(Volatile.Read(ref snapshots) >= 2);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DuplicateIdentityStopsLaterRuntimeAndManualRefreshProbesOnce()
    {
        string root = CreateRoot();
        try
        {
            string first = Directory.CreateDirectory(
                Path.Combine(root, "duplicate-identity-a")).FullName;
            string second = Directory.CreateDirectory(
                Path.Combine(root, "duplicate-identity-b")).FullName;
            int clientsCreated = 0;
            await using var coordinator = new CodexAccountsCoordinator(
                null,
                root,
                configuration =>
                {
                    Interlocked.Increment(ref clientsCreated);
                    return new(configuration);
                },
                notificationCooldown: TimeSpan.FromMinutes(1));
            var snapshots = new ConcurrentDictionary<string, CodexAccountSnapshot>();
            coordinator.SnapshotChanged += snapshot => snapshots[snapshot.AccountId] = snapshot;

            await coordinator.ConfigureAsync(Settings(first, second), null);
            await coordinator.RefreshAsync();

            Assert.Equal(2, clientsCreated);
            Assert.Equal(UsageAvailability.Available, snapshots["a"].Snapshot.Availability);
            Assert.Equal("DUPLICATE_ACCOUNT", snapshots["b"].Snapshot.Reason);

            await coordinator.RefreshAsync();

            Assert.Equal(3, clientsCreated);
            Assert.Equal("DUPLICATE_ACCOUNT", snapshots["b"].Snapshot.Reason);

            AppSettings settings = Settings(first, second);
            await coordinator.ConfigureAsync(settings with
            {
                CodexAccounts =
                [
                    settings.CodexAccounts[0],
                    settings.CodexAccounts[1] with { DisplayName = "Still duplicate" },
                ],
            }, null);
            Assert.Equal("DUPLICATE_ACCOUNT", snapshots["b"].Snapshot.Reason);

            await coordinator.ConfigureAsync(settings with
            {
                ShowCodexUsage = false,
                ShowAdditionalUsage = false,
                ShowCredits = false,
            }, null);
            await coordinator.DisposeAsync();
            await coordinator.DisposeAsync();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ReorderingDuplicateAccountsMakesTheFirstAccountAuthoritative()
    {
        string root = CreateRoot();
        try
        {
            string first = Directory.CreateDirectory(
                Path.Combine(root, "duplicate-identity-a")).FullName;
            string second = Directory.CreateDirectory(
                Path.Combine(root, "duplicate-identity-b")).FullName;
            await using var coordinator = new CodexAccountsCoordinator(
                null,
                root,
                notificationCooldown: TimeSpan.FromMinutes(1));
            var snapshots = new ConcurrentDictionary<string, CodexAccountSnapshot>();
            coordinator.SnapshotChanged += snapshot => snapshots[snapshot.AccountId] = snapshot;
            AppSettings settings = Settings(first, second);
            await coordinator.ConfigureAsync(settings, null);
            await coordinator.RefreshAsync();
            Assert.Equal(UsageAvailability.Available, snapshots["a"].Snapshot.Availability);
            Assert.Equal("DUPLICATE_ACCOUNT", snapshots["b"].Snapshot.Reason);

            await coordinator.ConfigureAsync(settings with
            {
                CodexAccounts =
                [
                    settings.CodexAccounts[1],
                    settings.CodexAccounts[0],
                ],
            }, null);
            await coordinator.RefreshAsync();

            Assert.Equal(UsageAvailability.Available, snapshots["b"].Snapshot.Availability);
            Assert.Equal("DUPLICATE_ACCOUNT", snapshots["a"].Snapshot.Reason);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task NotificationTriggeredReadIsBoundedToOneTrailingRefresh()
    {
        string root = CreateRoot();
        try
        {
            string account = Directory.CreateDirectory(Path.Combine(root, "account-a")).FullName;
            await using var coordinator = new CodexAccountsCoordinator(
                null,
                root,
                notificationCooldown: TimeSpan.FromMilliseconds(30),
                notificationDebounce: TimeSpan.FromMilliseconds(20));
            int snapshots = 0;
            coordinator.SnapshotChanged += _ => Interlocked.Increment(ref snapshots);
            await coordinator.ConfigureAsync(SingleSettings(account), null);

            await coordinator.RefreshAsync();
            await Task.Delay(200);

            Assert.Equal(2, Volatile.Read(ref snapshots));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task HomeLossClearsValuesAndRecoveryRecreatesOnlyThatClient()
    {
        string root = CreateRoot();
        try
        {
            string account = Directory.CreateDirectory(Path.Combine(root, "account-a")).FullName;
            int clientsCreated = 0;
            await using var coordinator = new CodexAccountsCoordinator(
                null,
                root,
                configuration =>
                {
                    Interlocked.Increment(ref clientsCreated);
                    return new(configuration);
                },
                notificationCooldown: TimeSpan.FromMinutes(1));
            CodexAccountSnapshot? latest = null;
            coordinator.SnapshotChanged += snapshot => latest = snapshot;
            await coordinator.ConfigureAsync(SingleSettings(account), null);
            await coordinator.RefreshAsync();
            Assert.NotNull(latest?.Snapshot.CreditSnapshot);

            Directory.Delete(account, true);
            await coordinator.RefreshAsync(CodexRefreshOrigin.Periodic);

            Assert.Equal("CODEX_HOME_UNAVAILABLE", latest?.Snapshot.Reason);
            Assert.Null(latest?.Snapshot.CreditSnapshot);
            Assert.Equal(2, clientsCreated);

            Directory.CreateDirectory(account);
            await coordinator.RefreshAsync();

            Assert.Equal(UsageAvailability.Available, latest?.Snapshot.Availability);
            Assert.NotNull(latest?.Snapshot.CreditSnapshot);
            Assert.Equal(2, clientsCreated);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TransportFailureAfterSuccessClearsAllCurrentComponents()
    {
        string root = CreateRoot();
        try
        {
            string account = Directory.CreateDirectory(
                Path.Combine(root, "stale-after-success")).FullName;
            await using var coordinator = new CodexAccountsCoordinator(
                null,
                root,
                notificationCooldown: TimeSpan.FromHours(1));
            CodexAccountSnapshot? latest = null;
            coordinator.SnapshotChanged += snapshot => latest = snapshot;
            await coordinator.ConfigureAsync(SingleSettings(account), null);

            await coordinator.RefreshAsync();
            Assert.NotEmpty(latest?.Snapshot.Windows ?? []);
            Assert.NotNull(latest?.Snapshot.CreditSnapshot);
            Assert.NotNull(latest?.Snapshot.IndividualLimit);

            await coordinator.RefreshAsync();

            Assert.Equal(UsageAvailability.Stale, latest?.Snapshot.Availability);
            Assert.True(latest?.Snapshot.IsStale);
            Assert.Empty(latest?.Snapshot.Windows ?? []);
            Assert.Null(latest?.Snapshot.CreditSnapshot);
            Assert.Null(latest?.Snapshot.IndividualLimit);
            Assert.NotNull(latest?.Snapshot.LastSuccessfulAt);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("RPC_TIMEOUT", true)]
    [InlineData("RPC_FAILURE", true)]
    [InlineData("METHOD_UNSUPPORTED", true)]
    [InlineData("SCHEMA_UNSUPPORTED", true)]
    [InlineData("CODEX_HOME_UNAVAILABLE", true)]
    [InlineData("CODEX_NOT_FOUND", true)]
    [InlineData("SIGNED_OUT", false)]
    [InlineData("DUPLICATE_ACCOUNT", false)]
    [InlineData("CODEX_HOME_DUPLICATE", false)]
    [InlineData("MONITORING_DISABLED", false)]
    [InlineData("UNKNOWN_REASON", false)]
    [InlineData(null, false)]
    public void SuccessfulTimestampRetentionMatchesReasonContract(
        string? reason,
        bool expected)
    {
        Assert.Equal(
            expected,
            CodexAccountsCoordinator.ShouldRetainSuccessfulAt(reason));
    }

    [Fact]
    public async Task SignedOutClearsHistorySoLaterRpcFailureCannotRestoreIt()
    {
        string root = CreateRoot();
        try
        {
            string account = Directory.CreateDirectory(
                Path.Combine(root, "signed-out-after-success")).FullName;
            await using var coordinator = new CodexAccountsCoordinator(
                null,
                root,
                notificationCooldown: TimeSpan.FromHours(1));
            CodexAccountSnapshot? latest = null;
            coordinator.SnapshotChanged += snapshot => latest = snapshot;
            await coordinator.ConfigureAsync(SingleSettings(account), null);

            await coordinator.RefreshAsync();
            Assert.Equal(UsageAvailability.Available, latest?.Snapshot.Availability);
            Assert.NotNull(latest?.Snapshot.LastSuccessfulAt);

            await coordinator.RefreshAsync();
            Assert.Equal("SIGNED_OUT", latest?.Snapshot.Reason);
            Assert.Null(latest?.Snapshot.LastSuccessfulAt);

            await coordinator.RefreshAsync();
            Assert.Equal("RPC_FAILURE", latest?.Snapshot.Reason);
            Assert.Null(latest?.Snapshot.LastSuccessfulAt);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void IdentityToStringAlwaysRedactsEmail()
    {
        var identity = new AccountIdentity("sentinel@example.test");

        string text = identity.ToString();

        Assert.DoesNotContain("sentinel", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
    }

    private static AppSettings Settings(string accountA, string accountB) => new()
    {
        CodexExecutablePath = FindFakeServer(),
        StartupTimeoutSeconds = 5,
        CodexAccounts =
        [
            new() { Id = "a", DisplayName = "A", CodexHomePath = accountA },
            new() { Id = "b", DisplayName = "B", CodexHomePath = accountB },
        ],
    };

    private static AppSettings SingleSettings(string account) => new()
    {
        CodexExecutablePath = FindFakeServer(),
        StartupTimeoutSeconds = 5,
        CodexAccounts =
        [
            new() { Id = "a", DisplayName = "A", CodexHomePath = account },
        ],
    };

    private static string CreateRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "coordinator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindFakeServer() => AiUsageMonitor.TestSupport.FakeExecutableLocator.FindCodexFakeAppServer();
}
