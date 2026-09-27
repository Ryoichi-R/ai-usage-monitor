using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Platform;
using AiUsageMonitor.Platform.Mac;

namespace AiUsageMonitor.Claude.Mac.Tests;

[SupportedOSPlatform("macos")]
public sealed class AcquisitionTests
{
    [Theory]
    [InlineData(".claude/remote-settings.json", false)]
    [InlineData("Application Support/ClaudeCode/managed-settings.json", true)]
    [InlineData("Application Support/ClaudeCode/managed-settings.d", true)]
    [InlineData("Application Support/ClaudeCode/managed-mcp.json", true)]
    [InlineData("Managed Preferences/com.anthropic.claudecode.plist", true)]
    public void ManagedMetadataBlocksLaunchWithoutReadingContents(string path, bool system)
    {
        using var temp = new Scratch();
        string library = Path.Combine(temp.Path, "Library");
        var policy = new ClaudeLaunchPolicy(temp.Path, library);
        Assert.True(policy.AllowsLaunch());
        string file = Path.Combine(system ? library : temp.Path, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "not JSON");
        Assert.False(policy.AllowsLaunch());
        Assert.Throws<InvalidOperationException>(() => policy.CreateStartInfo("/fake", temp.Path));
        Assert.Equal("MANAGED_SETTINGS_PRESENT", new MacClaudeExecutableLocator(temp.Path, "/missing", policy).Resolve(null).FailureReason);
    }

    [Fact]
    public void IsolationUsesFixedArgumentsAndOnlyApprovedEnvironment()
    {
        using var temp = new Scratch(); var policy = new ClaudeLaunchPolicy(temp.Path, Path.Combine(temp.Path, "Library"));
        var info = policy.CreateStartInfo("/fake", temp.Path);
        Assert.Equal(ClaudeLaunchPolicy.IsolationArguments, info.ArgumentList);
        Assert.Equal(7, info.Environment.Count); Assert.Equal("en_US.UTF-8", info.Environment["LANG"]);
        Assert.DoesNotContain("ANTHROPIC_API_KEY", info.Environment.Keys);
        Assert.Equal("CLAUDE_NOT_INSTALLED", new MacClaudeExecutableLocator(temp.Path, "/missing", policy).Resolve("/missing-explicit").FailureReason);
    }

    [Theory]
    [InlineData("2.1.274 (Claude Code)", true, true, null)]
    [InlineData("2.1.999 (Claude Code)", true, false, "CLI_VERSION_REVALIDATION_REQUIRED")]
    [InlineData("2.1.274", false, false, "REQUIRED_FLAG_MISSING")]
    public async Task CapabilityProbesAreSupervisedAndVersionBound(string version, bool flags, bool supported, string? reason)
    {
        using var temp = new Scratch(); var launcher = new ProbeLauncher(version, flags);
        var probe = new MacClaudeCapabilityProbe(launcher, new ClaudeLaunchPolicy(temp.Path, temp.Path), new Workspace(temp.Path));
        var result = await probe.ProbeAsync("/fake", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(supported, result.Supported); Assert.Equal(reason, result.FailureReason);
        Assert.Equal(launcher.Created.Count, launcher.Created.Count(x => x.Disposed));
    }

    [Fact]
    public async Task CapabilityTimeoutAndCallerCancellationDisposeChildren()
    {
        using var temp = new Scratch(); var launcher = new PendingLauncher();
        var probe = new MacClaudeCapabilityProbe(launcher, new ClaudeLaunchPolicy(temp.Path, temp.Path), new Workspace(temp.Path));
        Assert.Equal("CAPABILITY_TIMEOUT", (await probe.ProbeAsync("/fake", TimeSpan.FromMilliseconds(30), CancellationToken.None)).FailureReason);
        using var cancel = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ProbeAsync("/fake", TimeSpan.FromSeconds(1), cancel.Token));
        Assert.All(launcher.Sessions, item => Assert.True(item.Disposed));
    }

    [Fact]
    public async Task ScreenRequiresQuietCompleteReadyBeforeInputAndRejectsChangedState()
    {
        await using var io = new FakeSession(); await using var screen = new MacClaudeScreenSession(io);
        io.Output.Emit("[Screen Reader Mode: on via flag]\r\n$");
        Assert.False((await screen.ReadScreenAsync(TimeSpan.FromMilliseconds(40), CancellationToken.None)).Success);
        Assert.True((await screen.SendUsageCommandAsync(TimeSpan.FromSeconds(2), CancellationToken.None)).Success);
        Assert.Equal("/usage\r", io.InputText);
        io.Output.Emit("\r\ntrust this folder");
        Assert.False((await screen.SendUsageCommandAsync(TimeSpan.FromSeconds(2), CancellationToken.None)).Success);
        Assert.False((await screen.SendEscapeAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Success);
        Assert.Equal("/usage\r", io.InputText);
        io.Output.Emit("\u001b[2J");
        Assert.False((await screen.ReadScreenAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).Success);
    }

    [Fact]
    public async Task UsageAllowsEscapeAndClosedSessionReportsExited()
    {
        await using var io = new FakeSession(); await using var screen = new MacClaudeScreenSession(io);
        io.Output.Emit("Current session\r\n11% used");
        Assert.True((await screen.SendEscapeAsync(TimeSpan.FromSeconds(2), CancellationToken.None)).Success);
        Assert.Equal("\u001b", io.InputText);
        io.Output.End(); await Task.Delay(30);
        Assert.Equal(ClaudeScreenFailureCode.ProcessExited, (await screen.ReadScreenAsync(TimeSpan.FromSeconds(1), CancellationToken.None)).ReasonCode);
    }

    [Fact]
    public async Task FactoryRejectsManagedPolicyAndDisposesNormalSession()
    {
        using var temp = new Scratch(); var launcher = new PendingLauncher();
        var policy = new ClaudeLaunchPolicy(temp.Path, temp.Path);
        var factory = new MacClaudeScreenSessionFactory(launcher, policy);
        var result = await factory.StartAsync("/fake", temp.Path, CancellationToken.None);
        Assert.True(result.Success); await result.Value!.DisposeAsync(); Assert.True(launcher.Sessions[0].Disposed);
        Directory.CreateDirectory(Path.Combine(temp.Path, ".claude")); File.WriteAllText(Path.Combine(temp.Path, ".claude", "remote-settings.json"), "");
        Assert.Equal(ClaudeScreenFailureCode.UnsupportedPlatform, (await factory.StartAsync("/fake", temp.Path, CancellationToken.None)).ReasonCode);
    }

    [Fact]
    public async Task RealPtyUsesControllingTerminalAndTerminatesOnDispose()
    {
        using var temp = new Scratch();
        var launcher = new MacManagedProcessLauncher(Path.Combine(AppContext.BaseDirectory, "ai-usage-process-supervisor"), Path.Combine(temp.Path, "journal"), usePty: true);
        var info = new ProcessStartInfo("/bin/sh");
        info.ArgumentList.Add("-c"); info.ArgumentList.Add("test -t 0 && test -t 1 && printf 'PTY_OK\\n'; read input; printf 'RECEIVED:%s\\n' \"$input\"; sleep 30");
        await using var session = await launcher.StartAsync(info, CancellationToken.None);
        Assert.Equal("PTY_OK", await session.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        await session.StandardInput.WriteAsync("hello\r"); await session.StandardInput.FlushAsync();
        Assert.Equal("RECEIVED:hello", await session.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
    }

    internal sealed class Workspace(string path) : IClaudeWorkspaceProvisioner { public string WorkspacePath => path; public string EnsureWorkspace() => path; }
    private sealed class ProbeLauncher(string version, bool flags) : IManagedProcessLauncher
    {
        internal List<FakeSession> Created { get; } = [];
        public Task<IManagedProcessSession> StartAsync(ProcessStartInfo info, CancellationToken token)
        {
            var session = new FakeSession(); Created.Add(session);
            session.Output.Emit(info.ArgumentList[0] == "--version" ? version : flags ? "--settings " + string.Join(' ', ClaudeLaunchPolicy.IsolationArguments) : "none");
            session.Output.End(); return Task.FromResult<IManagedProcessSession>(session);
        }
    }
    internal sealed class PendingLauncher : IManagedProcessLauncher
    {
        internal List<FakeSession> Sessions { get; } = [];
        public Task<IManagedProcessSession> StartAsync(ProcessStartInfo info, CancellationToken token)
        { var session = new FakeSession(); Sessions.Add(session); return Task.FromResult<IManagedProcessSession>(session); }
    }
    internal sealed class FakeSession : IManagedProcessSession
    {
        internal FeedStream Output { get; } = new();
        private readonly MemoryStream _input = new();
        internal bool Disposed { get; private set; }
        internal string InputText => Encoding.UTF8.GetString(_input.ToArray());
        public StreamReader StandardOutput { get; }
        public StreamReader StandardError { get; } = new(new MemoryStream());
        public StreamWriter StandardInput { get; }
        internal FakeSession() { StandardOutput = new(Output); StandardInput = new(_input, new UTF8Encoding(false), leaveOpen: true); }
        public ValueTask DisposeAsync() { Disposed = true; Output.End(); return ValueTask.CompletedTask; }
    }
    internal sealed class FeedStream : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
        private byte[]? _pending; private int _offset;
        internal void Emit(string text) => _channel.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        internal void End() => _channel.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pending is null)
            {
                if (!await _channel.Reader.WaitToReadAsync(cancellationToken)) return 0;
                _pending = await _channel.Reader.ReadAsync(cancellationToken); _offset = 0;
            }
            int length = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsMemory(_offset, length).CopyTo(buffer); _offset += length;
            if (_offset == _pending.Length) _pending = null;
            return length;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

[SupportedOSPlatform("macos")]
internal sealed class Scratch : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine("/private/tmp", "aiusage-claude-test-" + Guid.NewGuid().ToString("N"));
    internal Scratch() => Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
}
