using System.Globalization;
using System.Text;
using System.Text.Json;
using AiUsageMonitor.Core.WorkStatus;
using AiUsageMonitor.Platform;
using AiUsageMonitor.WorkStatus;
using AiUsageMonitor.WorkStatus.Bridge;

namespace AiUsageMonitor.WorkStatus.Tests;

/// <summary>Plan §4.1-3: confirm Claude completion only when no other Stop hook can continue the turn, and retract it otherwise.</summary>
public sealed class ClaudeCompletionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 5, 0, 0, TimeSpan.Zero);

    private static string Fixture()
    {
        string path = Path.Combine(Environment.GetEnvironmentVariable("AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT") ?? AppContext.BaseDirectory,
            "work-status-completion", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static WorkStatusEvent Claude(WorkEventKind kind, int tick, bool retractable = false) =>
        new(1, $"{kind}-{tick}", WorkProvider.ClaudeCode, "s", "", kind, Now.AddSeconds(tick), true, tick, retractable);

    [Theory]
    [InlineData("""{}""", StopCompletionCheck.Eligible)]
    [InlineData("""{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"C:\\x\\ai-usage-work-status-bridge.exe","args":["ClaudeCode","C:\\s"]}]}]}}""", StopCompletionCheck.Eligible)]
    [InlineData("""{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"/Applications/A.app/Contents/MacOS/ai-usage-work-status-bridge"}]}]}}""", StopCompletionCheck.Eligible)]
    [InlineData("""{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"notify.sh","async":true}]}]}}""", StopCompletionCheck.Eligible)]
    [InlineData("""{"hooks":{"PreToolUse":[{"hooks":[{"type":"command","command":"guard.sh"}]}]}}""", StopCompletionCheck.Eligible)]
    [InlineData("""{"enabledPlugins":{"a@b":false}}""", StopCompletionCheck.Eligible)]
    [InlineData("""{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"review-gate.sh"}]}]}}""", StopCompletionCheck.OtherStopHook)]
    [InlineData("""{"hooks":{"Stop":[{"hooks":[{"type":"prompt","prompt":"check"}]}]}}""", StopCompletionCheck.OtherStopHook)]
    [InlineData("""{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"wake.sh","async":true,"asyncRewake":true}]}]}}""", StopCompletionCheck.OtherStopHook)]
    [InlineData("""{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"ai-usage-work-status-bridge-fake.exe"}]}]}}""", StopCompletionCheck.OtherStopHook)]
    [InlineData("""{"enabledPlugins":{"codex@openai":true}}""", StopCompletionCheck.PluginsEnabled)]
    [InlineData("""{"hooks":{"Stop":{}}}""", StopCompletionCheck.Unreadable)]
    [InlineData("""{broken""", StopCompletionCheck.Unreadable)]
    public void InspectorClassifiesSettingsDocuments(string json, StopCompletionCheck expected) =>
        Assert.Equal(expected, StopHookInspector.Inspect([json]));

    [Fact]
    public void InspectorReadsUserProjectLocalAndManagedFiles()
    {
        string root = Fixture();
        string project = Path.Combine(root, "project"), user = Path.Combine(root, "user"), managed = Path.Combine(root, "managed");
        Directory.CreateDirectory(Path.Combine(project, ".claude"));
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(Path.Combine(managed, "managed-settings.d"));
        Assert.Equal(StopCompletionCheck.Eligible, StopHookInspector.InspectProject(project, user, managed, () => false));
        Assert.Equal(StopCompletionCheck.ManagedPolicy, StopHookInspector.InspectProject(project, user, managed, () => true));

        string stop = """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"gate"}]}]}}""";
        foreach (string path in new[]
        {
            Path.Combine(user, "settings.json"),
            Path.Combine(project, ".claude", "settings.json"),
            Path.Combine(project, ".claude", "settings.local.json"),
            Path.Combine(managed, "managed-settings.json"),
            Path.Combine(managed, "managed-settings.d", "10-policy.json"),
        })
        {
            File.WriteAllText(path, stop);
            Assert.Equal(StopCompletionCheck.OtherStopHook, StopHookInspector.InspectProject(project, user, managed, () => false));
            File.WriteAllText(path, "{broken");
            Assert.Equal(StopCompletionCheck.Unreadable, StopHookInspector.InspectProject(project, user, managed, () => false));
            File.Delete(path);
        }
    }

    [Fact]
    public void ExamplesAddTheCompletionFlagOnlyToClaudeStop()
    {
        string root = Fixture(), bridge = Path.Combine(root, "ai-usage-work-status-bridge.exe");
        Assert.Throws<ArgumentException>(() => HookExamples.Create(WorkProvider.CodexDesktop, bridge, root, true, stopMayComplete: true));
        using var plain = JsonDocument.Parse(HookExamples.Create(WorkProvider.ClaudeCode, bridge, root, true, new(2, 1, 309)));
        Assert.Equal(2, Args(plain, "Stop").Length);
        using var complete = JsonDocument.Parse(HookExamples.Create(WorkProvider.ClaudeCode, bridge, root, true, new(2, 1, 309), true));
        Assert.Equal(["ClaudeCode", root, HookExamples.StopMayCompleteFlag], Args(complete, "Stop"));
        Assert.Equal(["ClaudeCode", root], Args(complete, "PreToolUse"));
        // The generated hook itself is recognized as the monitor, so re-checking the project stays eligible.
        Assert.Equal(StopCompletionCheck.Eligible, StopHookInspector.Inspect([complete.RootElement.GetRawText()]));
    }

    private static string[] Args(JsonDocument json, string name) =>
        [.. json.RootElement.GetProperty("hooks").GetProperty(name)[0].GetProperty("hooks")[0].GetProperty("args")
            .EnumerateArray().Select(value => value.GetString()!)];

    [Theory]
    [InlineData(false, true, "ClaudeCode", true, WorkSessionState.Completed)]
    [InlineData(true, true, "ClaudeCode", true, WorkSessionState.StopCandidate)]
    [InlineData(false, false, "ClaudeCode", true, WorkSessionState.StopCandidate)]
    [InlineData(false, true, "CodexDesktop", false, null)]
    public void BridgeConfirmsOnlyFlaggedInactiveClaudeStop(bool stopHookActive, bool flag, string provider,
        bool written, WorkSessionState? expected)
    {
        string root = Fixture();
        string body = JsonSerializer.Serialize(new { session_id = "s", hook_event_name = "Stop", stop_hook_active = stopHookActive });
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(body));
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        string[] args = flag ? [provider, root, HookExamples.StopMayCompleteFlag] : [provider, root];
        Assert.Equal(0, BridgeCommand.Execute(args, input, output, new Origin(), Now));
        Assert.Equal("{}", output.ToString().Trim());
        Assert.Equal(written, Directory.Exists(Path.Combine(root, "events")));
        if (expected is not null)
        {
            WorkStatusModel model = new WorkStatusCollector(new(root)).Poll(Now).Model;
            Assert.Equal(expected, Assert.Single(model.Sessions).State);
            Assert.Equal(expected == WorkSessionState.Completed, Assert.Single(model.Sessions).CompletionRetractable);
        }
    }

    [Fact]
    public void BridgeIgnoresUnknownThirdArgument()
    {
        string root = Fixture();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("""{"session_id":"s","hook_event_name":"Stop","stop_hook_active":false}"""));
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(0, BridgeCommand.Execute(["ClaudeCode", root, "--complete"], input, output, new Origin(), Now));
        Assert.False(Directory.Exists(Path.Combine(root, "events")));
    }

    [Fact]
    public void CodexStopNeverConfirmsEvenIfAsked()
    {
        using var json = JsonDocument.Parse("""{"session_id":"s","hook_event_name":"Stop","stop_hook_active":false}""");
        Assert.Equal(WorkEventKind.StopCandidate, HookNormalizer.Normalize(json.RootElement, WorkProvider.CodexDesktop, true, Now, true)!.Kind);
    }

    [Theory]
    [InlineData(WorkEventKind.Activity, WorkSessionState.Running)]
    [InlineData(WorkEventKind.Waiting, WorkSessionState.Waiting)]
    [InlineData(WorkEventKind.StopCandidate, WorkSessionState.StopCandidate)]
    public void ContinuationRetractsConfirmedCompletion(WorkEventKind next, WorkSessionState state)
    {
        var model = new WorkStatusModel();
        model.Apply(Claude(WorkEventKind.Started, 0));
        model.Apply(Claude(WorkEventKind.Completed, 1, retractable: true));
        Assert.Equal(WorkSessionState.Completed, Assert.Single(model.Notices).State);
        model.Apply(Claude(next, 2));
        Assert.Equal(state, Assert.Single(model.Sessions).State);
        Assert.DoesNotContain(model.Notices, notice => notice.State == WorkSessionState.Completed);
        Assert.False(model.Sessions[0].CompletionRetractable);
    }

    [Fact]
    public void NextPromptKeepsThePreviousCompletion()
    {
        var model = new WorkStatusModel();
        model.Apply(Claude(WorkEventKind.Started, 0));
        model.Apply(Claude(WorkEventKind.Completed, 1, retractable: true));
        model.Apply(Claude(WorkEventKind.Started, 2));
        model.Apply(Claude(WorkEventKind.Activity, 3));
        Assert.Equal(WorkSessionState.Completed, Assert.Single(model.Notices).State);
        Assert.Equal(WorkSessionState.Running, Assert.Single(model.Sessions).State);
    }

    [Fact]
    public void NonRetractableCompletionSurvivesALaterCandidate()
    {
        var model = new WorkStatusModel();
        model.Apply(Claude(WorkEventKind.Completed, 1));
        model.Apply(Claude(WorkEventKind.StopCandidate, 2));
        Assert.Contains(model.Notices, notice => notice.State == WorkSessionState.Completed);
    }

    private sealed class Origin : IDesktopOrigin
    {
        public bool IsDesktopAncestor(DesktopApplication application) => true;
    }
}
