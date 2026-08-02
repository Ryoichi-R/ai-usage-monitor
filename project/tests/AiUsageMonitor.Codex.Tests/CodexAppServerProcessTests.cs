using System.Reflection;
using AiUsageMonitor.Codex.Client;
using AiUsageMonitor.Codex.Process;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Codex.Tests;

public sealed class CodexAppServerProcessTests
{
    [Fact]
    public async Task StartsRealChildCompletesHandshakeAndDisposesTwice()
    {
        string executable = FindFakeServer();
        var server = new CodexAppServerProcess();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await server.StartAsync(executable, TimeSpan.FromSeconds(5), timeout.Token);
        await server.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public void ClientVersionComesFromAssemblyMetadata()
    {
        MethodInfo method = typeof(CodexAppServerProcess).GetMethod(
            "GetClientVersion",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        string actual = Assert.IsType<string>(method.Invoke(null, null));
        string expected = typeof(CodexAppServerProcess).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UsageClientRequiresFixedConfiguration()
    {
        ConstructorInfo constructor = Assert.Single(typeof(CodexUsageClient).GetConstructors());
        Assert.Equal(
            [typeof(CodexClientConfiguration)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        MethodInfo read = Assert.Single(
            typeof(CodexUsageClient).GetMethods(),
            method => method.Name == nameof(CodexUsageClient.ReadAsync));
        Assert.Equal(
            [typeof(CancellationToken)],
            read.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public async Task LifetimeGuardIsAttachedAndDisposedExactlyOnce()
    {
        int attachCount = 0;
        var guard = new CountingDisposable();
        var server = new CodexAppServerProcess();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await server.StartAsync(
            FindFakeServer(),
            null,
            TimeSpan.FromSeconds(5),
            _ =>
            {
                Interlocked.Increment(ref attachCount);
                return guard;
            },
            timeout.Token);
        await server.DisposeAsync();
        await server.DisposeAsync();

        Assert.Equal(1, attachCount);
        Assert.Equal(1, guard.DisposeCount);
    }

    [Fact]
    public async Task LifetimeGuardFailureStopsStartupAndServerRemainsDisposable()
    {
        var server = new CodexAppServerProcess();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.StartAsync(
                FindFakeServer(),
                null,
                TimeSpan.FromSeconds(5),
                _ => throw new InvalidOperationException("guard failed"),
                timeout.Token));
        await server.DisposeAsync();
        await server.DisposeAsync();

        Assert.Equal("guard failed", error.Message);
    }

    [Fact]
    public async Task UsageClientReadsAccountAndRateLimitsFromRealChild()
    {
        await using var client = new CodexUsageClient(new(
            FindFakeServer(),
            null,
            TimeSpan.FromSeconds(5)));
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.RateLimitsUpdated += () => updated.TrySetResult();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        UsageSnapshot result = await client.ReadAsync(timeout.Token);
        await updated.Task.WaitAsync(timeout.Token);

        Assert.Equal(UsageAvailability.Available, result.Availability);
        Assert.Equal("plus", result.PlanType);
        Assert.Single(result.Windows);
        Assert.Equal(25, result.Windows[0].UsedPercent);
        Assert.Equal(11.25m, result.CreditSnapshot?.Balance);
        Assert.Equal(2.50m, result.IndividualLimit?.Used);
    }

    [Fact]
    public async Task UsageClientReportsNotInstalledWhenExecutableMissing()
    {
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            await using var client = new CodexUsageClient(new(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-codex.exe"),
                null,
                TimeSpan.FromSeconds(1)));

            UsageSnapshot result = await client.ReadAsync(CancellationToken.None);

            Assert.Equal(UsageAvailability.NotInstalled, result.Availability);
            Assert.Equal("CODEX_NOT_FOUND", result.Reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public async Task FixedClientsKeepCodexHomesAndValuesIsolated()
    {
        string root = Path.Combine(Path.GetTempPath(), "codex-monitor-tests-" + Guid.NewGuid().ToString("N"));
        string accountA = Path.Combine(root, "account-a");
        string accountB = Path.Combine(root, "account-b");
        Directory.CreateDirectory(accountA);
        Directory.CreateDirectory(accountB);
        try
        {
            await using var clientA = new CodexUsageClient(new(
                FindFakeServer(),
                accountA,
                TimeSpan.FromSeconds(5)));
            await using var clientB = new CodexUsageClient(new(
                FindFakeServer(),
                accountB,
                TimeSpan.FromSeconds(5)));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            UsageSnapshot[] results = await Task.WhenAll(
                clientA.ReadAsync(timeout.Token),
                clientB.ReadAsync(timeout.Token));
            Assert.Equal(25, results[0].Windows[0].UsedPercent);
            Assert.Equal(70, results[1].Windows[0].UsedPercent);
            Assert.Equal(11.25m, results[0].CreditSnapshot?.Balance);
            Assert.Equal(22.50m, results[1].CreditSnapshot?.Balance);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string FindFakeServer() => AiUsageMonitor.TestSupport.FakeExecutableLocator.FindCodexFakeAppServer();

    private sealed class CountingDisposable : IDisposable
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
