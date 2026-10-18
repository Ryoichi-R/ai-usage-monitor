using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using AiUsageMonitor.Claude.Bridge.Mac;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Mac.Tests;

[SupportedOSPlatform("macos")]
public sealed class StatusLineTests
{
    [Theory]
    [InlineData("{\"protocol\":\"bad\"}", "PROTOCOL_MISMATCH")]
    [InlineData("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":1,\"resets_at\":\"bad\"}}}", "INVALID_RESET")]
    public void UntrustedNumericTypesReturnFailureWithoutThrowing(string json, string expected)
        => Assert.Equal(expected, AiUsageMonitor.Claude.StatusLine.ClaudeStatusLineParser.Parse(json, DateTimeOffset.UtcNow).Reason);

    [Fact]
    public async Task BridgeSendsOnlyMinimalProtocolAndListenerPublishes()
    {
        using var temp = new Scratch(); string socket = Path.Combine(temp.Path, "v1.sock");
        await using var listener = new ClaudeUsageSocketListener(socket);
        var received = new TaskCompletionSource<UsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ObservationReceived += value => received.TrySetResult(value);
        listener.Start(); listener.Start();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("""{"protocol":99,"version":"test","private":"discard","rate_limits":{"five_hour":{"used_percentage":11}}}"""));
        await ClaudeStatusLineBridge.RunAsync(input, socket);
        var snapshot = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(UsageAvailability.Available, snapshot.Availability);
        Assert.Equal(11, snapshot.Windows[0].UsedPercent);
        Assert.True(LocalSocketSecurity.Valid(socket, false));
        await listener.DisposeAsync(); Assert.False(File.Exists(socket));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    public void InvalidJsonIsDiscarded(string text) => Assert.Null(ClaudeStatusLineBridge.Minimize(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void PayloadLimitsVersionTypeAndShellQuotingAreEnforced()
    {
        Assert.Null(ClaudeStatusLineBridge.Minimize(new byte[16385]));
        using var json = JsonDocument.Parse(ClaudeStatusLineBridge.Minimize(Encoding.UTF8.GetBytes("""{"version":42,"secret":"discard"}"""))!);
        Assert.Equal(1, json.RootElement.GetProperty("protocol").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("version").ValueKind);
        Assert.False(json.RootElement.TryGetProperty("secret", out _));
        string expanded = "{\"version\":\"" + new string('<', 3000) + "\"}";
        Assert.Null(ClaudeStatusLineBridge.Minimize(Encoding.UTF8.GetBytes(expanded)));
        using var setup = JsonDocument.Parse(ClaudeStatusLineBridge.CreateSetupExample("/space 日本語/a'b"));
        Assert.Equal("'/space 日本語/a'\\''b'", setup.RootElement.GetProperty("statusLine").GetProperty("command").GetString());
        Assert.Throws<ArgumentException>(() => ClaudeStatusLineBridge.CreateSetupExample("relative"));
    }

    [Fact]
    public async Task BlockedInputAndAbsentServerFinishWithinDeadlineWithExitZero()
    {
        using var temp = new Scratch(); using var pending = new AcquisitionTests.FeedStream();
        var watch = Stopwatch.StartNew();
        await ClaudeStatusLineBridge.RunAsync(pending, Path.Combine(temp.Path, "absent.sock"));
        Assert.InRange(watch.Elapsed.TotalSeconds, .5, 3);
        Assert.Equal(0, await BridgeEntry.RunAsync(new MemoryStream()));
        await ClaudeStatusLineBridge.RunAsync(new MemoryStream(new byte[16385]), Path.Combine(temp.Path, "absent.sock"));
        await ClaudeStatusLineBridge.RunAsync(new MemoryStream(Encoding.UTF8.GetBytes("{}")), Path.Combine(temp.Path, "absent.sock"));
    }

    [Fact]
    public async Task UnsafeDirectoryFileAndSymlinkAreRejected()
    {
        using var temp = new Scratch(); string socket = Path.Combine(temp.Path, "v1.sock");
        File.SetUnixFileMode(temp.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
        await using (var unsafeListener = new ClaudeUsageSocketListener(socket)) Assert.Throws<IOException>(unsafeListener.Start);
        File.SetUnixFileMode(temp.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(socket, "sentinel");
        await using (var fileListener = new ClaudeUsageSocketListener(socket)) Assert.Throws<IOException>(fileListener.Start);
        Assert.Equal("sentinel", File.ReadAllText(socket)); File.Delete(socket);
        string target = Path.Combine(temp.Path, "target"); File.WriteAllText(target, "sentinel"); File.CreateSymbolicLink(socket, target);
        await using (var linkListener = new ClaudeUsageSocketListener(socket)) Assert.Throws<IOException>(linkListener.Start);
        Assert.Equal("sentinel", File.ReadAllText(target));
    }

    [Fact]
    public async Task StaleSocketIsRecoveredAndActiveListenerCannotBeReplaced()
    {
        using var temp = new Scratch(); string path = Path.Combine(temp.Path, "v1.sock");
        using (var stale = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            string original = Path.Combine(temp.Path, "original.sock");
            stale.Bind(new UnixDomainSocketEndPoint(original)); File.Move(original, path);
        }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await using var listener = new ClaudeUsageSocketListener(path); listener.Start();
        await using var second = new ClaudeUsageSocketListener(path); Assert.Throws<IOException>(second.Start);
        using var peer = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await peer.ConnectAsync(new UnixDomainSocketEndPoint(path));
        await listener.DisposeAsync();
    }

    [Fact]
    public async Task OversizedAndStalledPeersDoNotBlockFutureObservations()
    {
        using var temp = new Scratch(); string path = Path.Combine(temp.Path, "v1.sock");
        await using var listener = new ClaudeUsageSocketListener(path); listener.Start();
        var received = new TaskCompletionSource<UsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ObservationReceived += value => received.TrySetResult(value);
        using (var stalled = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            await stalled.ConnectAsync(new UnixDomainSocketEndPoint(path)); await Task.Delay(1200);
        }
        using (var peer = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            await peer.ConnectAsync(new UnixDomainSocketEndPoint(path)); await peer.SendAsync(new byte[16385]); peer.Shutdown(SocketShutdown.Send);
        }
        Assert.Equal("PAYLOAD_TOO_LARGE", (await received.Task.WaitAsync(TimeSpan.FromSeconds(2))).Reason);
    }
}
