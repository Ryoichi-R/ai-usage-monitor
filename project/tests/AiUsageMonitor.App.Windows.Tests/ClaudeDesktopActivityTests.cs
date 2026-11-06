using System.Text.Json;
using AiUsageMonitor.Core.WorkStatus;
using AiUsageMonitor.WorkStatus;

namespace AiUsageMonitor.App.Windows.Tests;

public sealed class ClaudeDesktopActivityTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_793_967_600);
    private const string SessionId = "f1fa3a4b-1234-4678-9123-1d2e3f4a5678";
    private static readonly string SessionHash = HookNormalizer.Digest(SessionId);
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;

    [Theory]
    [InlineData("busy", "claude-desktop", "interactive", 42, 1, true, true, true)]
    [InlineData("idle", "claude-desktop", "interactive", 42, 1, true, true, false)]
    [InlineData("waiting", "claude-desktop", "interactive", 42, 1, true, true, false)]
    [InlineData("busy", "cli", "interactive", 42, 1, true, true, false)]
    [InlineData("busy", "claude-desktop", "headless", 42, 1, true, true, false)]
    [InlineData("busy", "claude-desktop", "interactive", 43, 1, true, true, false)]
    [InlineData("busy", "claude-desktop", "interactive", 42, 0, true, true, false)]
    [InlineData("busy", "claude-desktop", "interactive", 42, 3, true, true, false)]
    [InlineData("busy", "claude-desktop", "interactive", 42, 1, false, true, false)]
    [InlineData("busy", "claude-desktop", "interactive", 42, 1, true, false, false)]
    public void AcceptsOnlyNewBusyStateOfTheSameConnectedLiveDesktopSession(
        string status, string entrypoint, string kind, int pid, int secondsAfterStop, bool live, bool connected, bool expected)
    {
        DateTimeOffset stoppedAt = Now.AddSeconds(-2);
        DateTimeOffset busySince = stoppedAt.AddSeconds(secondsAfterStop);
        string sessions = Directory.CreateDirectory(Path.Combine(_root, "sessions")).FullName;
        File.WriteAllText(Path.Combine(sessions, "42.json"), JsonSerializer.Serialize(new
        {
            pid,
            sessionId = SessionId,
            status,
            entrypoint,
            kind,
            statusUpdatedAt = busySince.ToUnixTimeMilliseconds(),
            procStart = 123456789L,
        }));
        File.WriteAllText(Path.Combine(sessions, "43.json"), "{");
        File.WriteAllText(Path.Combine(sessions, "42.peer.key"), "{");
        var source = new ClaudeDesktopActivity(_root, _root, (actualPid, start) => live && actualPid == 42 && start == 123456789L);
        WorkSession session = new(WorkProvider.ClaudeCode, connected ? SessionHash : "unmatched", "",
            WorkSessionState.StopCandidate, stoppedAt, 0);

        var states = source.ReadBusySince([session], Now);

        if (expected) Assert.Equal(busySince, states[SessionHash]);
        else Assert.Empty(states);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
