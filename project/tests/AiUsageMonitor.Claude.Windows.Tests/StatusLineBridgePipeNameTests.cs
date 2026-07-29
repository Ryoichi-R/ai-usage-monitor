using System.Diagnostics;
using System.Text;
using AiUsageMonitor.Claude.StatusLine;
using AiUsageMonitor.Claude.Transport;
using AiUsageMonitor.Core.Usage;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace AiUsageMonitor.Claude.Windows.Tests;

/// <summary>
/// 同梱bridgeの<c>-PipeName</c>契約を検証する。
/// active sourceは起動ごとの専用pipeを使い、省略時は常駐セッション用の固定pipeへ送る。
/// </summary>
public sealed class StatusLineBridgePipeNameTests
{
    private static string BridgePath =>
        Path.Combine(AppContext.BaseDirectory, "assets", "claude-statusline-bridge.ps1");

    [Fact]
    public void BridgeStaysAsciiOnly()
    {
        // Claude Codeはbridgeを Windows PowerShell 5.1 で起動する。5.1 はBOMなしファイルをANSIとして
        // 解読するため、非ASCII文字が混入するとparse自体が失敗し、bridgeが無言で終了する。
        byte[] bytes = File.ReadAllBytes(BridgePath);
        Assert.DoesNotContain(bytes, value => value > 127);
    }

    [Fact]
    public void BridgeDefaultPipeNameMatchesServerConstant()
    {
        string script = File.ReadAllText(BridgePath);
        Assert.Contains($"[string]$PipeName = '{ClaudeUsagePipeServer.PipeName}'", script, StringComparison.Ordinal);
        // 固定名のハードコードが残っていないこと（$PipeName 経由でのみ接続する）。
        Assert.DoesNotContain($"'.', '{ClaudeUsagePipeServer.PipeName}'", script, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task BridgeSendsPayloadToExplicitPipeName()
    {
        string pipeName = $"AiUsageMonitor-Claude-test-{Guid.NewGuid():N}";
        await using var server = new ClaudeUsagePipeServer(pipeName);
        var completion = new TaskCompletionSource<UsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ObservationReceived += snapshot => completion.TrySetResult(snapshot);
        server.Start();

        await RunBridgeAsync(pipeName, """{"version":"2.1.218","cwd":"secret","rate_limits":null}""");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        UsageSnapshot received = await completion.Task.WaitAsync(timeout.Token);
        Assert.Equal(UsageAvailability.Waiting, received.Availability);
        Assert.Equal("RATE_LIMITS_MISSING", received.Reason);
        Assert.Equal("2.1.218", received.ClientVersion);
    }

    [WindowsFact]
    public async Task BridgeExitsQuietlyWhenNoServerListens()
    {
        string pipeName = $"AiUsageMonitor-Claude-test-{Guid.NewGuid():N}";
        int exitCode = await RunBridgeAsync(pipeName, """{"version":"2.1.218","rate_limits":null}""");
        Assert.Equal(0, exitCode);
    }

    private static async Task<int> RunBridgeAsync(string pipeName, string payload)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(BridgePath);
        startInfo.ArgumentList.Add("-PipeName");
        startInfo.ArgumentList.Add(pipeName);

        using DiagnosticsProcess process = DiagnosticsProcess.Start(startInfo)!;
        await process.StandardInput.WriteAsync(payload);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        return process.ExitCode;
    }
}

/// <summary>Windows でのみ実行するテスト。</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows でのみ実行する。";
    }
}
