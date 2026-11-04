using System.Runtime.Versioning;
using System.Diagnostics;
using System.Text.Json;
using AiUsageMonitor.Core.WorkStatus;
using AiUsageMonitor.WorkStatus;
using AiUsageMonitor.Platform.Mac.WorkStatus;

namespace AiUsageMonitor.Platform.Mac.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacDesktopOriginTests
{
    [Theory]
    [InlineData("/Applications/Codex.app/Contents/MacOS/Codex", DesktopApplication.Codex, true)]
    [InlineData("/Applications/Codex.app/Contents/MacOS/ChatGPT", DesktopApplication.Codex, true)]
    [InlineData("/Applications/ChatGPT.app/Contents/MacOS/ChatGPT", DesktopApplication.Codex, true)]
    [InlineData("/Applications/Claude.app/Contents/MacOS/Claude", DesktopApplication.Claude, true)]
    [InlineData("/Applications/Claude.app/Contents/MacOS/Claude", DesktopApplication.Codex, false)]
    [InlineData("/Applications/ChatGPT.app/Contents/Resources/codex", DesktopApplication.Codex, false)]
    [InlineData("/Applications-other/Codex.app/Contents/MacOS/Codex", DesktopApplication.Codex, false)]
    public void PathOnlySelectsCandidates(string path, DesktopApplication app, bool expected) =>
        Assert.Equal(expected, MacDesktopOrigin.MatchesPath(path, app, "/Applications"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RenamedCodexRequiresVerifiedRunningIdentity(bool signature)
    {
        MacDesktopOrigin.ProcessIdentity? Read(int pid) => pid switch
        {
            30 => new(30, 20, 1, 0, "/bin/sh"),
            20 => new(20, 10, 2, 0, "/Applications/ChatGPT.app/Contents/MacOS/ChatGPT"),
            _ => null,
        };
        Assert.Equal(signature, MacDesktopOrigin.HasDesktopAncestor(30, DesktopApplication.Codex, "/Users/test/Applications", Read, (_, _) => signature));
    }

    [Fact]
    public void MonitorAncestorAndPidReuseAreRejected()
    {
        var monitor = new MacDesktopOrigin.ProcessIdentity(20, 10, 2, 0, "/test/AiUsageMonitor.App.Mac");
        MacDesktopOrigin.ProcessIdentity? Read(int pid) => pid == 30 ? new(30, 20, 1, 0, "/bin/sh") : monitor;
        Assert.False(MacDesktopOrigin.HasDesktopAncestor(30, DesktopApplication.Codex, "/Users/test/Applications", Read, (_, _) => throw new InvalidOperationException()));
        monitor = monitor with { Path = "/Applications/ChatGPT.app/Contents/MacOS/ChatGPT" };
        Assert.False(MacDesktopOrigin.HasDesktopAncestor(30, DesktopApplication.Codex, "/Users/test/Applications", Read, (_, _) =>
        {
            monitor = monitor with { StartMicroseconds = 1 };
            return true;
        }));
    }

    [Fact]
    public void UnavailableCyclicAndUnknownAncestorsFailClosed()
    {
        Assert.False(MacDesktopOrigin.HasDesktopAncestor(30, DesktopApplication.Codex, "/test", _ => null, (_, _) => true));
        Assert.False(MacDesktopOrigin.HasDesktopAncestor(30, DesktopApplication.Codex, "/test", pid => new(pid, pid, 1, 0, "/bin/sh"), (_, _) => true));
        Assert.False(MacDesktopOrigin.HasDesktopAncestor(30, DesktopApplication.Codex, "/test", pid => new(pid, pid + 1, 1, 0, "/bin/sh"), (_, _) => true));
    }

    [Fact]
    public async Task ShellExamplePassesLiteralPathsWithoutExpansion()
    {
        string root = Path.Combine(Path.GetTempPath(), "aiusage-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string bridge = Path.Combine(root, "bridge ' $literal `value`");
        string state = Path.Combine(root, "日本語 ' $literal `value` ; root");
        File.WriteAllText(bridge, "#!/bin/sh\nprintf '%s\\n' \"$1\" \"$2\"\n");
        File.SetUnixFileMode(bridge, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var example = JsonDocument.Parse(HookExamples.Create(WorkProvider.CodexDesktop, bridge, state, false));
        string command = example.RootElement.GetProperty("hooks").GetProperty("Stop")[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
        using var process = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", command])
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("CodexDesktop\n" + state + "\n", output);
        Assert.Equal("", await process.StandardError.ReadToEndAsync());
    }

    [Fact]
    public void NativeSignatureRejectsUnrelatedProcessAndMissingPid()
    {
        Assert.False(MacDesktopOrigin.VerifySignature(Environment.ProcessId, DesktopApplication.Codex));
        Assert.False(MacDesktopOrigin.VerifySignature(int.MaxValue, DesktopApplication.Claude));
    }
}
