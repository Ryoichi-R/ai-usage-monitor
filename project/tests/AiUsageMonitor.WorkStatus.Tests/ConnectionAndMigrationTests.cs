using System.Globalization;
using System.Text;
using System.Text.Json;
using AiUsageMonitor.Core.WorkStatus;
using AiUsageMonitor.Platform;
using AiUsageMonitor.WorkStatus;
using AiUsageMonitor.WorkStatus.Bridge;

namespace AiUsageMonitor.WorkStatus.Tests;

public sealed class ConnectionAndMigrationTests
{
    private static string Fixture()
    {
        string path = Path.Combine(Environment.GetEnvironmentVariable("AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT") ?? AppContext.BaseDirectory,
            "work-status-connections", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BridgeNeverReturnsDecisionsOrPrivateData(bool verified)
    {
        string root = Fixture();
        byte[] bytes = Encoding.UTF8.GetBytes("""{"session_id":"synthetic-session","turn_id":"synthetic-turn","hook_event_name":"Stop","last_assistant_message":"synthetic-private-response","prompt":"synthetic-private-prompt"}""");
        using var input = new MemoryStream(bytes);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(0, BridgeCommand.Execute(["CodexDesktop", root], input, output, new Origin(verified), DateTimeOffset.UtcNow));
        Assert.Equal(bytes.Length, input.Position);
        Assert.Equal("{}", output.ToString().Trim());
        WorkStatusModel model = new WorkStatusCollector(new(root)).Poll(DateTimeOffset.UtcNow).Model;
        Assert.Equal(verified ? 1 : 0, model.Notices.Count);
        Assert.DoesNotContain("synthetic-", JsonSerializer.Serialize(model));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"session_id\":\"s\",\"hook_event_name\":\"SubagentStop\"}")]
    public void InvalidBridgeInputIsNonBlocking(string text)
    {
        string root = Fixture();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(0, BridgeCommand.Execute(["ClaudeCode", root], input, output, new Origin(true), DateTimeOffset.UtcNow));
        Assert.Equal("{}", output.ToString().Trim());
        Assert.False(Directory.Exists(Path.Combine(root, "events")));
    }

    [Fact]
    public void BridgeRejectsOversizeAndUnknownProvider()
    {
        string root = Fixture();
        using var input = new MemoryStream(new byte[4 * 1024 * 1024 + 1]);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(0, BridgeCommand.Execute(["CodexDesktop", root], input, output, new Origin(true), DateTimeOffset.UtcNow));
        Assert.False(Directory.Exists(Path.Combine(root, "events")));
        Assert.Equal(0, BridgeCommand.Execute(["Unknown", root], Stream.Null, output, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ClaudeExamplesUseArgumentsContinueAndVersionGate()
    {
        string root = Fixture(), bridge = Path.Combine(root, "a ' $ bridge.exe");
        Assert.Throws<ArgumentException>(() => HookExamples.Create(WorkProvider.ClaudeCode, bridge, root, true));
        Assert.Throws<ArgumentException>(() => HookExamples.Create(WorkProvider.ClaudeCode, bridge, root, true, new(2, 1, 294)));
        using var json = JsonDocument.Parse(HookExamples.Create(WorkProvider.ClaudeCode, bridge, root, true, new(2, 1, 309)));
        JsonElement hook = json.RootElement.GetProperty("hooks").GetProperty("Stop")[0].GetProperty("hooks")[0];
        Assert.Equal(bridge, hook.GetProperty("command").GetString());
        Assert.Equal("continue", hook.GetProperty("onFailure").GetString());
        Assert.Equal(root, hook.GetProperty("args")[1].GetString());
    }

    [Fact]
    public void CodexShellExamplesQuotePathsAndDoNotMutateSettings()
    {
        string root = Fixture(), bridge = Path.Combine(root, "a ' $ bridge.exe");
        using var json = JsonDocument.Parse(HookExamples.Create(WorkProvider.CodexDesktop, bridge, root, true));
        string command = json.RootElement.GetProperty("hooks").GetProperty("Stop")[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
        string decoded = Encoding.Unicode.GetString(Convert.FromBase64String(command.Split(' ')[^1]));
        Assert.Contains(bridge.Replace("'", "''", StringComparison.Ordinal), decoded);
        Assert.DoesNotContain("pwsh", command);
        using var mac = JsonDocument.Parse(HookExamples.Create(WorkProvider.CodexDesktop, "/Applications/A ' B.app/bridge", "/Users/test/work root", false));
        Assert.Contains("exec '", mac.RootElement.GetProperty("hooks").GetProperty("Stop")[0].GetProperty("hooks")[0].GetProperty("command").GetString());
        Assert.Empty(Directory.GetFiles(root));
    }

    [Fact]
    public void DeploymentKeepsReferencedBuildAndHashesDependencies()
    {
        string root = Fixture(), package = Path.Combine(root, "package"), managed = Path.Combine(root, "managed");
        Directory.CreateDirectory(package);
        string name = OperatingSystem.IsWindows() ? "ai-usage-work-status-bridge.exe" : "ai-usage-work-status-bridge";
        File.WriteAllText(Path.Combine(package, name), "synthetic-executable");
        string dependency = Path.Combine(package, "core.dll");
        File.WriteAllText(dependency, "before");
        string first = BridgeDeployment.Prepare(package, managed);
        Assert.Equal(first, BridgeDeployment.Prepare(package, managed));
        File.WriteAllText(dependency, "after");
        string second = BridgeDeployment.Prepare(package, managed);
        if (!OperatingSystem.IsMacOS())
        {
            Assert.NotEqual(first, second);
            Assert.Equal("before", File.ReadAllText(Path.Combine(Path.GetDirectoryName(first)!, "core.dll")));
        }
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Throws<IOException>(() => BridgeDeployment.Prepare(Path.Combine(root, "absent"), managed));
    }

    [Fact]
    public void LegacyImportIsIdempotentPreservesOldBytesAndAcknowledgement()
    {
        string root = Fixture(), old = Path.Combine(root, "old");
        Directory.CreateDirectory(old);
        const string text = """{"Version":1,"Sessions":[],"Notices":{"CodexDesktop":[{"Id":"legacy-event","SessionId":"legacy-session","TurnId":"legacy-turn","State":"Completed","At":"2026-11-03T00:00:00Z"}]}}""";
        File.WriteAllText(Path.Combine(old, "state.json"), text);
        var collector = new WorkStatusCollector(new(Path.Combine(root, "new")));
        WorkNotice notice = Assert.Single(collector.ImportLegacy(old).Model.Notices);
        collector.Acknowledge([notice.Id]);
        Assert.Empty(collector.ImportLegacy(old).Model.Notices);
        Assert.Equal(text, File.ReadAllText(Path.Combine(old, "state.json")));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "new", "legacy-backups")));
    }

    [Fact]
    public void RunningOrInvalidLegacyStateDoesNotChangeDestination()
    {
        string root = Fixture(), old = Path.Combine(root, "old");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "state.json"), "{broken");
        var collector = new WorkStatusCollector(new(Path.Combine(root, "new")));
        Assert.ThrowsAny<JsonException>(() => collector.ImportLegacy(old));
        using (var held = new FileStream(Path.Combine(old, "monitor.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => collector.ImportLegacy(old));
        Assert.Empty(collector.Current.Model.Notices);
        Assert.False(File.Exists(Path.Combine(root, "new", "state.json")));
    }

    [Fact]
    public void ErrorLogIsBoundedAndNeverUsesExceptionMessageOrData()
    {
        string root = Fixture();
        var error = new InvalidOperationException("synthetic-private-message");
        error.Data["input"] = "synthetic-private-data";
        WorkStatusErrorLog.Write(root, new ArgumentException(), 512);
        for (int i = 0; i < 20; i++) WorkStatusErrorLog.Write(root, error, 512);
        string path = Path.Combine(root, "error.log"), text = File.ReadAllText(path);
        Assert.True(new FileInfo(path).Length <= 512);
        Assert.DoesNotContain("synthetic-private", text);
        Assert.DoesNotContain("ArgumentException", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("HResult=0x", text);
    }

    private sealed class Origin(bool verified) : IDesktopOrigin
    {
        public bool IsDesktopAncestor(DesktopApplication application) => verified;
    }
}
