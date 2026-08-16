using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Claude.Windows.Cli;
using AiUsageMonitor.Claude.Windows.Console;
using AiUsageMonitor.Claude.Windows.Process;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Windows.Tests;

public sealed class HiddenConsoleIntegrationTests
{
    [WindowsFact]
    public async Task HiddenConsoleRoundTripAllowsOnlyUsageAndCleansProcess()
    {
        string executable = FindFakeCli();
        string directory = Path.Combine(
            Path.GetTempPath(),
            "AiUsageMonitor.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string settings = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(settings, "{}");
        int processId = 0;
        try
        {
            await using (HiddenConsoleSession session =
                         HiddenConsoleSession.Start(executable, directory, settings))
            {
                processId = session.Process.Id;
                var helper = new ConsoleHelperClient(executable);
                ConsoleHelperResponse ready = await WaitForAsync(
                    () => helper.ReadAsync(processId, TimeSpan.FromSeconds(5), CancellationToken.None),
                    response => response.Ok &&
                        ClaudeCliScreenStateMachine.Classify(response.Lines) == ClaudeCliScreenSignature.Ready);
                Assert.True(ready.Ok);

                ConsoleHelperResponse sent = await helper.SendUsageAsync(
                    processId,
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None);
                Assert.True(sent.Ok);
                ConsoleHelperResponse usage = await WaitForAsync(
                    () => helper.ReadAsync(processId, TimeSpan.FromSeconds(5), CancellationToken.None),
                    response => response.Ok &&
                        ClaudeCliScreenStateMachine.Classify(response.Lines) == ClaudeCliScreenSignature.UsageScreen);
                Assert.Equal(ClaudeCliScreenSignature.UsageScreen,
                    ClaudeCliScreenStateMachine.Classify(usage.Lines));
            }

            await Task.Delay(100);
            Assert.Throws<ArgumentException>(() =>
                System.Diagnostics.Process.GetProcessById(processId));
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(directory);
        }
    }

    [WindowsFact]
    public async Task ActiveSourceReceivesOnlyItsPrivatePipePayload()
    {
        string executable = FindFakeCli();
        string root = Path.Combine(
            Path.GetTempPath(),
            "AiUsageMonitor.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string bridge = Path.Combine(root, "claude-statusline-bridge.ps1");
        await File.WriteAllTextAsync(bridge, "# test bridge");
        try
        {
            var source = new ClaudeCliActiveSource(
                null,
                bridge,
                TimeSpan.FromSeconds(15),
                new ClaudeWorkspaceProvisioner(root),
                new ConsoleHelperClient(executable),
                _ => new ClaudeExecutableInfo(executable, "2.1.218", true, "Anthropic, PBC", null),
                (_, _, _) => Task.FromResult(new ClaudeCliCapabilities(true, "2.1.218", null)));

            var observation = await source.RefreshAsync(CancellationToken.None);

            Assert.Equal(UsageAvailability.Available, observation.Availability);
            Assert.Equal([48d, 32d], observation.Snapshot.Windows.Select(window => window.UsedPercent));
            Assert.Equal(52d, observation.Snapshot.Windows[0].RemainingPercent);
            Assert.Equal(68d, observation.Snapshot.Windows[1].RemainingPercent);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [Theory]
    [InlineData("trust", UsageAvailability.Setup, "CLAUDE_TRUST_REQUIRED")]
    [InlineData("setup", UsageAvailability.Setup, "CLAUDE_TRUST_REQUIRED")]
    [InlineData("signed-out", UsageAvailability.SignedOut, "CLAUDE_SIGNED_OUT")]
    [InlineData("usage", UsageAvailability.Unsupported, "UNEXPECTED_USAGE_SCREEN")]
    [InlineData("exit", UsageAvailability.Unavailable, "PROCESS_EXITED")]
    public async Task ActiveSourceMapsSafeTerminalScreenStates(
        string mode,
        UsageAvailability expectedAvailability,
        string expectedReason)
    {
        string root = await CreateActiveSourceRootAsync(mode);
        try
        {
            ClaudeCliActiveSource source = CreateActiveSource(root, TimeSpan.FromSeconds(8));

            var observation = await source.RefreshAsync(CancellationToken.None);

            Assert.Equal(expectedAvailability, observation.Availability);
            Assert.Equal(expectedReason, observation.Reason);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [WindowsFact]
    public async Task ActiveSourceFallsBackToStrictUsageScreenWhenStatusLineIsSilent()
    {
        string root = await CreateActiveSourceRootAsync("no-payload");
        try
        {
            ClaudeCliActiveSource source = CreateActiveSource(root, TimeSpan.FromSeconds(2));

            var observation = await source.RefreshAsync(CancellationToken.None);

            Assert.Equal(UsageAvailability.Available, observation.Availability);
            Assert.Equal(ClaudeUsageSourceKind.CliScreen, observation.Source);
            Assert.Equal([48d, 32d], observation.Snapshot.Windows.Select(window => window.UsedPercent));
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [WindowsFact]
    public async Task ActiveSourceWaitsForACompleteIncrementallyRenderedUsageScreen()
    {
        string root = await CreateActiveSourceRootAsync("partial-usage");
        try
        {
            ClaudeCliActiveSource source = CreateActiveSource(root, TimeSpan.FromSeconds(3));

            ClaudeUsageObservation observation =
                await source.RefreshAsync(CancellationToken.None);

            Assert.Equal(UsageAvailability.Available, observation.Availability);
            Assert.Equal([48d, 32d], observation.Snapshot.Windows.Select(window => window.UsedPercent));
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root);
        }
    }

    [WindowsFact]
    public async Task ActiveSourceReturnsConsoleBufferReadFailedWhenHelperReadKeepsFailing()
    {
        // CLIプロセス自体は正常に起動しReadyになるが、helper経由のscreen buffer読取りだけが
        // 一貫して失敗するケース。認証は壊れていないため、汎用READY_TIMEOUTではなく
        // 取得経路の異常として区別できるCONSOLE_BUFFER_READ_FAILEDを返す。
        string root = await CreateActiveSourceRootAsync("ready");
        string readFailingHelper = CreateFakeCliVariant("helper-read-fails");
        try
        {
            var source = new ClaudeCliActiveSource(
                null,
                Path.Combine(root, "claude-statusline-bridge.ps1"),
                TimeSpan.FromSeconds(2),
                new ClaudeWorkspaceProvisioner(root),
                new ConsoleHelperClient(readFailingHelper),
                _ => new ClaudeExecutableInfo(FindFakeCli(), "2.1.218", true, "Anthropic, PBC", null),
                (_, _, _) => Task.FromResult(new ClaudeCliCapabilities(true, "2.1.218", null)));

            var observation = await source.RefreshAsync(CancellationToken.None);

            Assert.Equal(UsageAvailability.Unavailable, observation.Availability);
            Assert.Equal("CONSOLE_BUFFER_READ_FAILED", observation.Reason);
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root);
            await DeleteFileWithRetryAsync(readFailingHelper);
        }
    }

    [WindowsFact]
    public async Task ActiveSourceMapsLocatorCapabilityAndWorkspaceFailures()
    {
        string executable = FindFakeCli();
        string missingBridge = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "bridge.ps1");
        string blockingRoot = Path.GetTempFileName();
        var notInstalled = new ClaudeCliActiveSource(
            null,
            missingBridge,
            TimeSpan.FromSeconds(2),
            resolveExecutable: _ => new("", null, false, null, "CLAUDE_NOT_INSTALLED"));
        var untrusted = new ClaudeCliActiveSource(
            null,
            missingBridge,
            TimeSpan.FromSeconds(2),
            resolveExecutable: _ => new(executable, null, false, null, "UNTRUSTED_EXECUTABLE"));
        var unsupported = new ClaudeCliActiveSource(
            null,
            missingBridge,
            TimeSpan.FromSeconds(2),
            resolveExecutable: _ => new(executable, "2.1.218", true, "Anthropic, PBC", null),
            probeCapabilities: (_, _, _) =>
                Task.FromResult(new ClaudeCliCapabilities(false, "2.1.218", "REQUIRED_FLAG_MISSING")));
        var workspaceFailure = new ClaudeCliActiveSource(
            null,
            missingBridge,
            TimeSpan.FromSeconds(2),
            new ClaudeWorkspaceProvisioner(blockingRoot),
            resolveExecutable: _ => new(executable, "2.1.218", true, "Anthropic, PBC", null),
            probeCapabilities: (_, _, _) =>
                Task.FromResult(new ClaudeCliCapabilities(true, "2.1.218", null)));

        try
        {
            ClaudeUsageObservation missing = await notInstalled.RefreshAsync(CancellationToken.None);
            ClaudeUsageObservation rejected = await untrusted.RefreshAsync(CancellationToken.None);
            ClaudeUsageObservation incompatible = await unsupported.RefreshAsync(CancellationToken.None);
            ClaudeUsageObservation provision = await workspaceFailure.RefreshAsync(CancellationToken.None);

            Assert.Equal(UsageAvailability.NotInstalled, missing.Availability);
            Assert.Equal(UsageAvailability.Unsupported, rejected.Availability);
            Assert.Equal("REQUIRED_FLAG_MISSING", incompatible.Reason);
            Assert.Equal("WORKSPACE_PROVISION_FAILED", provision.Reason);
        }
        finally
        {
            File.Delete(blockingRoot);
        }
    }

    private static async Task<ConsoleHelperResponse> WaitForAsync(
        Func<Task<ConsoleHelperResponse>> read,
        Func<ConsoleHelperResponse, bool> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        ConsoleHelperResponse? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await read();
            if (predicate(last)) return last;
            await Task.Delay(100);
        }
        return last ?? new(false, "NO_RESPONSE", [], 0, 0, 0);
    }

    private static string FindFakeCli() => AiUsageMonitor.TestSupport.FakeExecutableLocator.FindClaudeFakeCli();

    private static string CreateFakeCliVariant(string name)
    {
        string source = FindFakeCli();
        string variant = Path.Combine(
            Path.GetDirectoryName(source)!,
            $"{name}-{Guid.NewGuid():N}.exe");
        File.Copy(source, variant);
        return variant;
    }

    private static async Task DeleteFileWithRetryAsync(string path)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception exception) when (
                (exception is IOException or UnauthorizedAccessException) && attempt < 19)
            {
                await Task.Delay(100);
            }
        }
    }

    private static async Task<string> CreateActiveSourceRootAsync(string mode)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "AiUsageMonitor.Tests",
            Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(root, "CodexUsageMonitor", "ClaudeCliWorkspace");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "fake-mode.txt"), mode);
        await File.WriteAllTextAsync(Path.Combine(root, "claude-statusline-bridge.ps1"), "# test bridge");
        return root;
    }

    private static ClaudeCliActiveSource CreateActiveSource(string root, TimeSpan timeout)
    {
        string executable = FindFakeCli();
        return new ClaudeCliActiveSource(
            null,
            Path.Combine(root, "claude-statusline-bridge.ps1"),
            timeout,
            new ClaudeWorkspaceProvisioner(root),
            new ConsoleHelperClient(executable),
            _ => new ClaudeExecutableInfo(executable, "2.1.218", true, "Anthropic, PBC", null),
            (_, _, _) => Task.FromResult(new ClaudeCliCapabilities(true, "2.1.218", null)));
    }

    private static async Task DeleteDirectoryWithRetryAsync(string path)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                return;
            }
            catch (IOException) when (attempt < 19)
            {
                await Task.Delay(100);
            }
        }
    }
}
