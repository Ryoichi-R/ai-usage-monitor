using System.Diagnostics;
using System.Runtime.Versioning;
using System.Xml.Linq;

namespace AiUsageMonitor.Platform.Mac.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacServicesTests
{
    [Fact]
    public void LaunchAgentEscapesArgumentsAndCanBeDisabledWithoutTouchingOtherFiles()
    {
        string root = NewRoot();
        try
        {
            string sentinel = Path.Combine(root, "unrelated.plist");
            File.WriteAllText(sentinel, "keep");
            var service = new LaunchAgentStartupService(root, () => ["/Applications/My & App/monitor", "a<b"]);
            service.Apply(true);
            XDocument document = XDocument.Load(service.PlistPath);
            Assert.Contains(document.Descendants("string"), element => element.Value == "a<b");
            Assert.Contains(document.Descendants("string"), element => element.Value == "/Applications/My & App/monitor");
            string before = File.ReadAllText(service.PlistPath);
            service.Apply(true);
            Assert.Equal(before, File.ReadAllText(service.PlistPath));
            service.Apply(false);
            service.Apply(false);
            Assert.False(File.Exists(service.PlistPath));
            Assert.Equal("keep", File.ReadAllText(sentinel));
            var invalid = new LaunchAgentStartupService(root, () => ["relative"]);
            Assert.Throws<InvalidOperationException>(() => invalid.Apply(true));
            Assert.Equal(["/usr/local/bin/dotnet", "/app/monitor.dll"], LaunchAgentStartupService.ResolveProgramArguments("/usr/local/bin/dotnet", "/app/monitor.dll"));
            Assert.Equal(["/app/monitor"], LaunchAgentStartupService.ResolveProgramArguments("/app/monitor", ""));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InstanceLockRejectsCompetitorAndReleasesAfterDispose()
    {
        string root = NewRoot();
        try
        {
            string path = Path.Combine(root, "instance.lock");
            using var first = new MacSingleInstanceGuard(path);
            using var second = new MacSingleInstanceGuard(path);
            Assert.True(first.TryAcquire());
            Assert.True(first.TryAcquire());
            Assert.False(second.TryAcquire());
            first.Dispose();
            Assert.True(second.TryAcquire());
            Assert.Throws<ObjectDisposedException>(() => first.TryAcquire());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void PathsRemainUnderExplicitHomeAndUnknownDisplayHandlesAreRejected()
    {
        var paths = new MacAppPathProvider("/test-home");
        Assert.StartsWith("/test-home/Library/Application Support/AiUsageMonitor/", paths.SettingsFilePath, StringComparison.Ordinal);
        Assert.StartsWith(paths.ApplicationSupportDirectory, paths.ClaudeWorkspaceDirectory, StringComparison.Ordinal);
        Assert.StartsWith(paths.ApplicationSupportDirectory, paths.TemporaryDirectory, StringComparison.Ordinal);
        Assert.EndsWith("instance.lock", paths.InstanceLockPath, StringComparison.Ordinal);
        Assert.Equal("/test-home/Library/LaunchAgents", paths.LaunchAgentsDirectory);
        Assert.Null(MacDisplayIdentity.TryGetStableId(0, MacDisplayIdentity.PlatformHandleDescriptor));
        Assert.Null(MacDisplayIdentity.TryGetStableId(1, "not-a-display"));
    }

    [Fact]
    public async Task ManagedLauncherOwnsStdioAndStopsIgnoringChildWithinDeadline()
    {
        string root = NewRoot();
        try
        {
            var launcher = new MacManagedProcessLauncher(Path.Combine(AppContext.BaseDirectory, "ai-usage-process-supervisor"), root);
            var info = new ProcessStartInfo("/bin/sh");
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("trap '' TERM HUP; printf 'ready\\n'; while :; do sleep 1; done");
            var session = await launcher.StartAsync(info, CancellationToken.None);
            Assert.Equal("ready", await session.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(ManagedProcessOutcome.Unknown, session.Outcome);
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(9));
            await session.DisposeAsync();
            Assert.Equal(ManagedProcessOutcome.Clean, session.Outcome);
            await launcher.SweepAsync(CancellationToken.None);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => launcher.StartAsync(info, cancelled.Token));
            var missing = new MacManagedProcessLauncher(Path.Combine(root, "missing"), root);
            await Assert.ThrowsAsync<IOException>(() => missing.StartAsync(info, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ManagedLauncherReportsDescendantThatLeavesTheSessionAndTerminatesIt()
    {
        string root = NewRoot();
        try
        {
            var launcher = new MacManagedProcessLauncher(Path.Combine(AppContext.BaseDirectory, "ai-usage-process-supervisor"), root);
            var info = new ProcessStartInfo("/usr/bin/perl");
            info.ArgumentList.Add("-MPOSIX");
            info.ArgumentList.Add("-e");
            info.ArgumentList.Add("$| = 1; my $pid = fork(); if ($pid == 0) { POSIX::setsid(); sleep 30; exit 0 } print \"$pid\\n\"; sleep 30");
            var session = await launcher.StartAsync(info, CancellationToken.None);
            int escaped = int.Parse(await session.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(9));
            Assert.Equal(ManagedProcessOutcome.DescendantEscaped, session.Outcome);
            // 離脱した子孫は(pid, 起動時刻)を照合したうえで個別に終了済みである。
            Assert.True(escaped > 1);
            Assert.False(IsAlive(escaped));
        }
        finally { Directory.Delete(root, true); }
    }

    private static bool IsAlive(int pid)
    {
        using var ps = Process.Start(new ProcessStartInfo("/bin/ps", ["-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "-o", "stat="]) { RedirectStandardOutput = true })!;
        string state = ps.StandardOutput.ReadToEnd().Trim();
        ps.WaitForExit();
        return state.Length > 0 && !state.StartsWith('Z');
    }

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "aiusage-platform-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return root;
    }
}
