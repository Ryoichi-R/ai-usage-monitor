using System.Collections.Concurrent;
using AiUsageMonitor.Codex.Client;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Codex.Runtime;

public enum CodexRefreshOrigin
{
    Manual,
    Periodic,
    Notification,
    Probe,
}

public sealed class CodexAccountsCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan DefaultNotificationCooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultNotificationDebounce = TimeSpan.FromMilliseconds(500);

    private readonly Func<CodexClientConfiguration, CodexUsageClient> _clientFactory;
    private readonly ConcurrentDictionary<string, AccountRuntime> _runtimes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BlockedAccount> _blockedAccounts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CodexHomePathResolver _homeResolver;
    private readonly TimeProvider _timeProvider;
    private readonly Func<double> _pollingJitterFactory;
    private readonly TimeSpan _notificationCooldown;
    private readonly TimeSpan _notificationDebounce;
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private readonly SemaphoreSlim _configurationChanged = new(0, 1);
    private readonly SemaphoreSlim _resultGate = new(1, 1);
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<string> _accountOrder = [];
    private long _pollingIntervalTicks = TimeSpan.FromMinutes(5).Ticks;
    private long _generation;
    private int _disposed;

    public CodexAccountsCoordinator(
        string? inheritedCodexHome,
        string userProfilePath,
        Func<CodexClientConfiguration, CodexUsageClient>? clientFactory = null,
        TimeProvider? timeProvider = null,
        Func<double>? pollingJitterFactory = null,
        TimeSpan? notificationCooldown = null,
        TimeSpan? notificationDebounce = null)
    {
        _homeResolver = new(inheritedCodexHome, userProfilePath);
        _clientFactory = clientFactory ?? (configuration => new CodexUsageClient(configuration));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollingJitterFactory = pollingJitterFactory ?? Random.Shared.NextDouble;
        _notificationCooldown = notificationCooldown ?? DefaultNotificationCooldown;
        _notificationDebounce = notificationDebounce ?? DefaultNotificationDebounce;
    }

    public event Action<CodexAccountSnapshot>? SnapshotChanged;

    public async Task ConfigureAsync(
        AppSettings settings,
        Func<System.Diagnostics.Process, IDisposable>? lifetimeGuardFactory,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(settings);
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppSettings normalized = settings.Normalized();
            Interlocked.Exchange(
                ref _pollingIntervalTicks,
                TimeSpan.FromSeconds(normalized.RefreshIntervalSeconds).Ticks);
            bool featuresEnabled = normalized.ShowCodexUsage ||
                normalized.ShowAdditionalUsage ||
                normalized.ShowCredits;
            CodexAccountSettings[] enabledAccounts = featuresEnabled
                ? normalized.CodexAccounts.Where(account => account.Enabled).ToArray()
                : [];
            _accountOrder = normalized.CodexAccounts.Select(account => account.Id).ToArray();
            long generation = Interlocked.Increment(ref _generation);
            var desired = new HashSet<string>(
                enabledAccounts.Select(account => account.Id),
                StringComparer.OrdinalIgnoreCase);
            var effectiveHomes = new HashSet<string>(CodexHomePathResolver.PathComparer);
            var toDispose = new List<AccountRuntime>();

            foreach (CodexAccountSettings account in enabledAccounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? effectiveHome = _homeResolver.TryResolveEffectivePath(account.CodexHomePath);
                var configuration = new CodexClientConfiguration(
                    normalized.CodexExecutablePath,
                    account.CodexHomePath is null ? null : effectiveHome,
                    TimeSpan.FromSeconds(normalized.StartupTimeoutSeconds),
                    lifetimeGuardFactory);

                string? reason = effectiveHome is null ||
                    (account.CodexHomePath is not null && !Directory.Exists(effectiveHome))
                    ? "CODEX_HOME_UNAVAILABLE"
                    : !effectiveHomes.Add(effectiveHome)
                        ? "CODEX_HOME_DUPLICATE"
                        : null;
                if (reason is not null)
                {
                    RemoveRuntime(account.Id, toDispose);
                    _blockedAccounts[account.Id] = new(
                        account,
                        configuration,
                        reason,
                        generation);
                    PublishUnavailable(account, reason);
                    continue;
                }

                if (_blockedAccounts.TryGetValue(account.Id, out BlockedAccount? blocked) &&
                    blocked.Reason == "DUPLICATE_ACCOUNT" &&
                    blocked.Configuration == configuration)
                {
                    _blockedAccounts[account.Id] = blocked with
                    {
                        Account = account,
                        Generation = generation,
                    };
                    PublishUnavailable(account, blocked.Reason);
                    continue;
                }

                _blockedAccounts.TryRemove(account.Id, out _);
                if (_runtimes.TryGetValue(account.Id, out AccountRuntime? existing) &&
                    existing.Configuration == configuration)
                {
                    existing.UpdatePresentation(account, generation);
                    continue;
                }

                RemoveRuntime(account.Id, toDispose);
                _runtimes[account.Id] = CreateRuntime(
                    account,
                    configuration,
                    _clientFactory(configuration),
                    generation);
            }

            foreach (AccountRuntime runtime in _runtimes.Values.Where(
                runtime => !desired.Contains(runtime.Account.Id)).ToArray())
            {
                RemoveRuntime(runtime.Account.Id, toDispose);
            }
            foreach (string blockedId in _blockedAccounts.Keys.Where(
                blockedId => !desired.Contains(blockedId)).ToArray())
            {
                _blockedAccounts.TryRemove(blockedId, out _);
            }

            await Task.WhenAll(toDispose.Select(runtime => runtime.DisposeAsync().AsTask()))
                .ConfigureAwait(false);
            SignalConfigurationChanged();
        }
        finally
        {
            _configurationGate.Release();
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(CodexRefreshOrigin.Manual, cancellationToken);

    public Task RunPeriodicPollingAsync(CancellationToken cancellationToken = default) =>
        RunPeriodicPollingCoreAsync(null, cancellationToken);

    internal Task RunPeriodicPollingAsync(
        TimeSpan pollingInterval,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            pollingInterval,
            TimeSpan.Zero);
        return RunPeriodicPollingCoreAsync(pollingInterval, cancellationToken);
    }

    private async Task RunPeriodicPollingCoreAsync(
        TimeSpan? pollingIntervalOverride,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        CancellationToken token = linked.Token;
        var dueByAccount = new Dictionary<string, DateTimeOffset>(
            StringComparer.OrdinalIgnoreCase);
        long observedGeneration = -1;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            long generation = Volatile.Read(ref _generation);
            if (generation != observedGeneration)
            {
                string[] configured = OrderedConfiguredAccountIds();
                TimeSpan pollingInterval = pollingIntervalOverride ?? PollingInterval;
                DateTimeOffset firstDue = _timeProvider.GetUtcNow() + pollingInterval;
                dueByAccount = configured
                    .Select((accountId, index) => new
                    {
                        Id = accountId,
                        Due = firstDue + GetInitialStagger(index, configured.Length),
                    })
                    .ToDictionary(item => item.Id, item => item.Due, StringComparer.OrdinalIgnoreCase);
                observedGeneration = generation;
            }

            while (_configurationChanged.Wait(0, token))
            {
            }
            if (Volatile.Read(ref _generation) != observedGeneration)
                continue;

            if (dueByAccount.Count == 0)
            {
                await _configurationChanged.WaitAsync(token).ConfigureAwait(false);
                continue;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            DateTimeOffset nextDue = dueByAccount.Values.Min();
            if (nextDue > now &&
                await WaitForConfigurationChangeAsync(nextDue - now, token).ConfigureAwait(false))
                continue;

            await RecoverHomeAccountsAsync(token).ConfigureAwait(false);
            now = _timeProvider.GetUtcNow();
            string[] dueIds = dueByAccount
                .Where(item => item.Value <= now)
                .Select(item => item.Key)
                .ToArray();
            var dueIdSet = dueIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            AccountRuntime[] dueRuntimes = OrderedRuntimes()
                .Where(runtime => dueIdSet.Contains(runtime.Account.Id))
                .ToArray();
            PendingSnapshot?[] results = await Task.WhenAll(dueRuntimes.Select(
                runtime => ReadOneAsync(runtime, CodexRefreshOrigin.Periodic, token)))
                .ConfigureAwait(false);
            await ApplyResultsAsync(results.OfType<PendingSnapshot>(), token)
                .ConfigureAwait(false);

            now = _timeProvider.GetUtcNow();
            foreach (string accountId in dueIds)
            {
                dueByAccount[accountId] = now + GetPollingInterval(
                    pollingIntervalOverride ?? PollingInterval,
                    _pollingJitterFactory());
            }
        }
    }

    public async Task RefreshAsync(
        CodexRefreshOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);

        if (origin is CodexRefreshOrigin.Manual or CodexRefreshOrigin.Periodic)
            await RecoverHomeAccountsAsync(linked.Token).ConfigureAwait(false);
        string[] duplicateProbeIds = origin == CodexRefreshOrigin.Manual
            ? OrderedBlocked()
                .Where(blocked => blocked.Reason == "DUPLICATE_ACCOUNT")
                .Select(blocked => blocked.Account.Id)
                .ToArray()
            : [];

        AccountRuntime[] runtimes = OrderedRuntimes();
        Task<PendingSnapshot?>[] refreshes = runtimes
            .Select((runtime, index) => RefreshWithOptionalStaggerAsync(
                runtime,
                origin,
                origin == CodexRefreshOrigin.Periodic
                    ? GetInitialStagger(index, runtimes.Length)
                    : TimeSpan.Zero,
                linked.Token))
            .ToArray();
        PendingSnapshot?[] results = await Task.WhenAll(refreshes).ConfigureAwait(false);
        await ApplyResultsAsync(results.OfType<PendingSnapshot>(), linked.Token)
            .ConfigureAwait(false);
        if (duplicateProbeIds.Length > 0)
            await ProbeDuplicateAccountsAsync(duplicateProbeIds, linked.Token)
                .ConfigureAwait(false);
    }

    public static TimeSpan GetInitialStagger(int index, int accountCount)
    {
        if (accountCount <= 1)
            return TimeSpan.Zero;
        double windowSeconds = Math.Min(2d * (accountCount - 1), 10d);
        return TimeSpan.FromSeconds(windowSeconds * index / (accountCount - 1));
    }

    public static TimeSpan GetPollingInterval(TimeSpan baseInterval, double jitterSample)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baseInterval, TimeSpan.Zero);
        double boundedSample = Math.Clamp(jitterSample, 0d, 1d);
        return TimeSpan.FromTicks((long)Math.Round(
            baseInterval.Ticks * (0.9d + (boundedSample * 0.2d)),
            MidpointRounding.AwayFromZero));
    }

    private TimeSpan PollingInterval =>
        TimeSpan.FromTicks(Interlocked.Read(ref _pollingIntervalTicks));

    private async Task<bool> WaitForConfigurationChangeAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task delayTask = Task.Delay(delay, _timeProvider, waitCancellation.Token);
        Task configurationTask = _configurationChanged.WaitAsync(waitCancellation.Token);
        Task completed = await Task.WhenAny(delayTask, configurationTask).ConfigureAwait(false);
        waitCancellation.Cancel();
        try
        {
            await Task.WhenAll(delayTask, configurationTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested)
        {
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ReferenceEquals(completed, configurationTask);
    }

    private void SignalConfigurationChanged()
    {
        try
        {
            _configurationChanged.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task<PendingSnapshot?> RefreshWithOptionalStaggerAsync(
        AccountRuntime runtime,
        CodexRefreshOrigin origin,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        return await ReadOneAsync(runtime, origin, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PendingSnapshot?> ReadOneAsync(
        AccountRuntime runtime,
        CodexRefreshOrigin origin,
        CancellationToken cancellationToken)
    {
        if (!await runtime.Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return null;

        DateTimeOffset completedAt = _timeProvider.GetUtcNow();
        bool pendingAuthoritativeNotification = false;
        try
        {
            runtime.CurrentReadOrigin = origin;
            CodexAccountReadResult result = await runtime.Client
                .ReadAccountAsync(cancellationToken)
                .ConfigureAwait(false);
            completedAt = _timeProvider.GetUtcNow();
            if (!IsCurrent(runtime))
                return null;

            runtime.IdentityEmail = result.Identity.Email;
            bool successfulSnapshot =
                result.Snapshot.Availability == UsageAvailability.Available &&
                string.IsNullOrWhiteSpace(result.Snapshot.Reason);
            if (!successfulSnapshot &&
                !ShouldRetainSuccessfulAt(result.Snapshot.Reason))
            {
                runtime.LastSuccessfulAt = null;
            }
            UsageSnapshot snapshot = NormalizeFailure(runtime, result.Snapshot);
            if (successfulSnapshot)
            {
                runtime.LastGood = result.Snapshot;
                runtime.LastSuccessfulReadAt = completedAt;
                runtime.LastSuccessfulAt = result.Snapshot.LastSuccessfulAt ??
                    result.Snapshot.ReceivedAt;
            }

            if (result.Snapshot.Reason == "CODEX_HOME_UNAVAILABLE")
            {
                runtime.IdentityEmail = null;
                runtime.LastGood = null;
                await runtime.ReplaceClientAsync(_clientFactory(runtime.Configuration))
                    .ConfigureAwait(false);
            }

            return new(runtime, snapshot);
        }
        finally
        {
            pendingAuthoritativeNotification = runtime.TakeAuthoritativeNotification();
            runtime.CurrentReadOrigin = null;
            runtime.Gate.Release();
            if (pendingAuthoritativeNotification)
            {
                DateTimeOffset due = origin is CodexRefreshOrigin.Manual or CodexRefreshOrigin.Periodic
                    ? completedAt + _notificationCooldown + _notificationDebounce
                    : completedAt + _notificationDebounce;
                ScheduleNotificationRefresh(runtime, due, replaceExisting: true);
            }
        }
    }

    private static UsageSnapshot NormalizeFailure(
        AccountRuntime runtime,
        UsageSnapshot snapshot)
    {
        bool retainSuccessfulAt = ShouldRetainSuccessfulAt(snapshot.Reason);
        if (!retainSuccessfulAt || runtime.LastSuccessfulAt is null)
            return snapshot;

        return snapshot with
        {
            Availability = snapshot.Reason is "RPC_TIMEOUT" or "RPC_FAILURE"
                ? UsageAvailability.Stale
                : snapshot.Availability,
            IsStale = snapshot.Reason is "RPC_TIMEOUT" or "RPC_FAILURE",
            Windows = [],
            Credits = null,
            CreditSnapshot = null,
            IndividualLimit = null,
            LastSuccessfulAt = runtime.LastSuccessfulAt,
        };
    }

    internal static bool ShouldRetainSuccessfulAt(string? reason) =>
        reason is
            "RPC_TIMEOUT" or
            "RPC_FAILURE" or
            "METHOD_UNSUPPORTED" or
            "SCHEMA_UNSUPPORTED" or
            "CODEX_HOME_UNAVAILABLE" or
            "CODEX_NOT_FOUND";

    private async Task ApplyResultsAsync(
        IEnumerable<PendingSnapshot> pendingResults,
        CancellationToken cancellationToken)
    {
        await _resultGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PendingSnapshot[] results = pendingResults
                .Where(result => IsCurrent(result.Runtime))
                .ToArray();
            var duplicateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AccountRuntime runtime in OrderedRuntimes())
            {
                if (runtime.IdentityEmail is { } email && !emails.Add(email))
                    duplicateIds.Add(runtime.Account.Id);
            }

            var toDispose = new List<AccountRuntime>();
            foreach (string duplicateId in duplicateIds)
            {
                if (!_runtimes.TryRemove(duplicateId, out AccountRuntime? duplicate))
                    continue;
                _blockedAccounts[duplicateId] = new(
                    duplicate.Account,
                    duplicate.Configuration,
                    "DUPLICATE_ACCOUNT",
                    duplicate.Generation);
                toDispose.Add(duplicate);
                PublishUnavailable(duplicate.Account, "DUPLICATE_ACCOUNT");
            }

            foreach (PendingSnapshot result in results)
            {
                if (!duplicateIds.Contains(result.Runtime.Account.Id) &&
                    IsCurrent(result.Runtime))
                    Publish(result.Runtime.Account, result.Snapshot);
            }

            await Task.WhenAll(toDispose.Select(runtime => runtime.DisposeAsync().AsTask()))
                .ConfigureAwait(false);
        }
        finally
        {
            _resultGate.Release();
        }
    }

    private void OnNotification(AccountRuntime runtime)
    {
        if (!IsCurrent(runtime))
            return;

        CodexRefreshOrigin? origin = runtime.CurrentReadOrigin;
        if (origin == CodexRefreshOrigin.Notification)
            return;
        if (origin is CodexRefreshOrigin.Manual or CodexRefreshOrigin.Periodic)
        {
            runtime.MarkAuthoritativeNotification();
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset due = runtime.LastSuccessfulReadAt is { } last &&
            now < last + _notificationCooldown
            ? last + _notificationCooldown + _notificationDebounce
            : now + _notificationDebounce;
        ScheduleNotificationRefresh(runtime, due, replaceExisting: false);
    }

    private void ScheduleNotificationRefresh(
        AccountRuntime runtime,
        DateTimeOffset due,
        bool replaceExisting)
    {
        if (!IsCurrent(runtime))
            return;
        runtime.ScheduleNotification(
            due,
            replaceExisting,
            _timeProvider,
            async cancellationToken =>
            {
                PendingSnapshot? result = await ReadOneAsync(
                    runtime,
                    CodexRefreshOrigin.Notification,
                    cancellationToken).ConfigureAwait(false);
                if (result is not null)
                    await ApplyResultsAsync([result], cancellationToken).ConfigureAwait(false);
            },
            _lifetime.Token);
    }

    private Task RecoverHomeAccountsAsync(CancellationToken cancellationToken)
    {
        BlockedAccount[] candidates = OrderedBlocked()
            .Where(blocked => blocked.Reason == "CODEX_HOME_UNAVAILABLE" &&
                blocked.Configuration.CodexHomePath is { } home &&
                Directory.Exists(home))
            .ToArray();
        foreach (BlockedAccount blocked in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_blockedAccounts.TryRemove(blocked.Account.Id, out _))
                continue;
            _runtimes[blocked.Account.Id] = CreateRuntime(
                blocked.Account,
                blocked.Configuration,
                _clientFactory(blocked.Configuration),
                blocked.Generation);
        }
        return Task.CompletedTask;
    }

    private async Task ProbeDuplicateAccountsAsync(
        IReadOnlyCollection<string> accountIds,
        CancellationToken cancellationToken)
    {
        await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var requestedIds = new HashSet<string>(
                accountIds,
                StringComparer.OrdinalIgnoreCase);
            var emailOwners = OrderedRuntimes()
                .Where(runtime => runtime.IdentityEmail is not null)
                .ToDictionary(
                    runtime => runtime.IdentityEmail!,
                    runtime => runtime,
                    StringComparer.OrdinalIgnoreCase);
            var order = _accountOrder
                .Select((id, index) => (id, index))
                .ToDictionary(pair => pair.id, pair => pair.index, StringComparer.OrdinalIgnoreCase);
            foreach (BlockedAccount blocked in OrderedBlocked().Where(
                blocked => blocked.Reason == "DUPLICATE_ACCOUNT" &&
                    requestedIds.Contains(blocked.Account.Id)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_blockedAccounts.TryGetValue(
                    blocked.Account.Id,
                    out BlockedAccount? currentBlocked) ||
                    currentBlocked != blocked)
                    continue;

                CodexUsageClient client = _clientFactory(blocked.Configuration);
                bool promoted = false;
                try
                {
                    CodexAccountReadResult result = await client
                        .ReadAccountAsync(cancellationToken)
                        .ConfigureAwait(false);
                    string? email = result.Identity.Email;
                    bool uncertain = email is null &&
                        result.Snapshot.Reason is "RPC_TIMEOUT" or "RPC_FAILURE";
                    if (uncertain)
                    {
                        PublishUnavailable(blocked.Account, "DUPLICATE_ACCOUNT");
                        continue;
                    }

                    AccountRuntime? owner = null;
                    if (email is not null)
                        emailOwners.TryGetValue(email, out owner);
                    int blockedIndex = order.GetValueOrDefault(
                        blocked.Account.Id,
                        int.MaxValue);
                    int ownerIndex = owner is null
                        ? int.MaxValue
                        : order.GetValueOrDefault(owner.Account.Id, int.MaxValue);
                    if (owner is not null && ownerIndex < blockedIndex)
                    {
                        PublishUnavailable(blocked.Account, "DUPLICATE_ACCOUNT");
                        continue;
                    }

                    var runtime = CreateRuntime(
                        blocked.Account,
                        blocked.Configuration,
                        client,
                        blocked.Generation);
                    runtime.IdentityEmail = email;
                    if (result.Snapshot.Availability == UsageAvailability.Available)
                    {
                        runtime.LastGood = result.Snapshot;
                        runtime.LastSuccessfulReadAt = _timeProvider.GetUtcNow();
                    }
                    _runtimes[blocked.Account.Id] = runtime;
                    _blockedAccounts.TryRemove(blocked.Account.Id, out _);
                    promoted = true;
                    if (email is not null)
                        emailOwners[email] = runtime;
                    Publish(blocked.Account, NormalizeFailure(runtime, result.Snapshot));

                    if (owner is not null &&
                        _runtimes.TryRemove(owner.Account.Id, out AccountRuntime? removedOwner) &&
                        ReferenceEquals(owner, removedOwner))
                    {
                        _blockedAccounts[owner.Account.Id] = new(
                            owner.Account,
                            owner.Configuration,
                            "DUPLICATE_ACCOUNT",
                            owner.Generation);
                        PublishUnavailable(owner.Account, "DUPLICATE_ACCOUNT");
                        await owner.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (!promoted)
                        await client.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private AccountRuntime CreateRuntime(
        CodexAccountSettings account,
        CodexClientConfiguration configuration,
        CodexUsageClient client,
        long generation) =>
        new(account, configuration, client, generation, OnNotification);

    private AccountRuntime[] OrderedRuntimes()
    {
        var order = _accountOrder
            .Select((id, index) => (id, index))
            .ToDictionary(pair => pair.id, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        return _runtimes.Values
            .OrderBy(runtime => order.TryGetValue(runtime.Account.Id, out int index)
                ? index
                : int.MaxValue)
            .ToArray();
    }

    private BlockedAccount[] OrderedBlocked()
    {
        var order = _accountOrder
            .Select((id, index) => (id, index))
            .ToDictionary(pair => pair.id, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        return _blockedAccounts.Values
            .OrderBy(blocked => order.TryGetValue(blocked.Account.Id, out int index)
                ? index
                : int.MaxValue)
            .ToArray();
    }

    private string[] OrderedConfiguredAccountIds()
    {
        var configuredIds = new HashSet<string>(
            _runtimes.Keys.Concat(_blockedAccounts.Keys),
            StringComparer.OrdinalIgnoreCase);
        return _accountOrder.Where(configuredIds.Contains).ToArray();
    }

    private bool IsCurrent(AccountRuntime runtime) =>
        _runtimes.TryGetValue(runtime.Account.Id, out AccountRuntime? current) &&
        ReferenceEquals(runtime, current);

    private void RemoveRuntime(string accountId, ICollection<AccountRuntime> toDispose)
    {
        if (_runtimes.TryRemove(accountId, out AccountRuntime? runtime))
            toDispose.Add(runtime);
    }

    private void PublishUnavailable(CodexAccountSettings account, string reason)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        Publish(
            account,
            new(
                UsageProvider.Codex,
                now,
                now,
                UsageAvailability.Unavailable,
                reason,
                null,
                [],
                null,
                false,
                null));
    }

    private void Publish(CodexAccountSettings account, UsageSnapshot snapshot) =>
        SnapshotChanged?.Invoke(new(
            account.Id,
            account.DisplayName,
            account.ShowInWidget,
            snapshot));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lifetime.Cancel();
        AccountRuntime[] runtimes = _runtimes.Values.ToArray();
        _runtimes.Clear();
        _blockedAccounts.Clear();
        await Task.WhenAll(runtimes.Select(runtime => runtime.DisposeAsync().AsTask()))
            .ConfigureAwait(false);
        _configurationGate.Dispose();
        _configurationChanged.Dispose();
        _resultGate.Dispose();
        _probeGate.Dispose();
        _lifetime.Dispose();
    }

    private sealed record PendingSnapshot(
        AccountRuntime Runtime,
        UsageSnapshot Snapshot);

    private sealed record BlockedAccount(
        CodexAccountSettings Account,
        CodexClientConfiguration Configuration,
        string Reason,
        long Generation);

    private sealed class AccountRuntime : IAsyncDisposable
    {
        private readonly Action<AccountRuntime> _notificationCallback;
        private readonly Action _notificationHandler;
        private readonly object _notificationSync = new();
        private CancellationTokenSource? _notificationCts;
        private Task _notificationTask = Task.CompletedTask;
        private DateTimeOffset? _notificationDue;
        private int _authoritativeNotification;
        private int _disposed;

        public AccountRuntime(
            CodexAccountSettings account,
            CodexClientConfiguration configuration,
            CodexUsageClient client,
            long generation,
            Action<AccountRuntime> notificationCallback)
        {
            Account = account;
            Configuration = configuration;
            Client = client;
            Generation = generation;
            _notificationCallback = notificationCallback;
            _notificationHandler = () => _notificationCallback(this);
            Client.RateLimitsUpdated += _notificationHandler;
        }

        public CodexAccountSettings Account { get; private set; }
        public CodexClientConfiguration Configuration { get; }
        public CodexUsageClient Client { get; private set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public UsageSnapshot? LastGood { get; set; }
        public DateTimeOffset? LastSuccessfulAt { get; set; }
        public DateTimeOffset? LastSuccessfulReadAt { get; set; }
        public string? IdentityEmail { get; set; }
        public long Generation { get; private set; }
        public CodexRefreshOrigin? CurrentReadOrigin { get; set; }

        public void UpdatePresentation(CodexAccountSettings account, long generation)
        {
            Account = account;
            Generation = generation;
        }

        public void MarkAuthoritativeNotification() =>
            Interlocked.Exchange(ref _authoritativeNotification, 1);

        public bool TakeAuthoritativeNotification() =>
            Interlocked.Exchange(ref _authoritativeNotification, 0) != 0;

        public async Task ReplaceClientAsync(CodexUsageClient replacement)
        {
            Client.RateLimitsUpdated -= _notificationHandler;
            CodexUsageClient previous = Client;
            Client = replacement;
            Client.RateLimitsUpdated += _notificationHandler;
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        public void ScheduleNotification(
            DateTimeOffset due,
            bool replaceExisting,
            TimeProvider timeProvider,
            Func<CancellationToken, Task> callback,
            CancellationToken coordinatorCancellation)
        {
            lock (_notificationSync)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;
                if (_notificationCts is not null &&
                    !replaceExisting &&
                    _notificationDue <= due)
                    return;

                _notificationCts?.Cancel();
                _notificationCts?.Dispose();
                _notificationCts = CancellationTokenSource.CreateLinkedTokenSource(
                    coordinatorCancellation);
                _notificationDue = due;
                CancellationToken token = _notificationCts.Token;
                _notificationTask = RunScheduledAsync(due, timeProvider, callback, token);
            }
        }

        private async Task RunScheduledAsync(
            DateTimeOffset due,
            TimeProvider timeProvider,
            Func<CancellationToken, Task> callback,
            CancellationToken cancellationToken)
        {
            try
            {
                TimeSpan delay = due - timeProvider.GetUtcNow();
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
                await callback(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                lock (_notificationSync)
                {
                    if (_notificationCts?.Token == cancellationToken)
                    {
                        _notificationCts.Dispose();
                        _notificationCts = null;
                        _notificationDue = null;
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            Task notificationTask;
            lock (_notificationSync)
            {
                _notificationCts?.Cancel();
                notificationTask = _notificationTask;
            }
            try
            {
                await notificationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            Client.RateLimitsUpdated -= _notificationHandler;
            await Client.DisposeAsync().ConfigureAwait(false);
            lock (_notificationSync)
            {
                _notificationCts?.Dispose();
                _notificationCts = null;
            }
            Gate.Dispose();
        }
    }
}
