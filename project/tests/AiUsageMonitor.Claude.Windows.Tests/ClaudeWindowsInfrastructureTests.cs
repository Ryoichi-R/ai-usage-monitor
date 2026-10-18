using System.Text.Json;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Claude.Windows.Console;
using AiUsageMonitor.Claude.Windows.Process;
using AiUsageMonitor.Platform.Windows;

namespace AiUsageMonitor.Claude.Windows.Tests;

public sealed class ClaudeWindowsInfrastructureTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void MissingConfiguredExecutableIsNotInstalled()
    {
        ClaudeExecutableInfo result = ClaudeExecutableLocator.Resolve(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "claude.exe"));
        Assert.False(result.SignatureValid);
        Assert.Equal("CLAUDE_NOT_INSTALLED", result.FailureReason);
    }

    [WindowsFact]
    public void SignedNonAnthropicExecutableIsRejected()
    {
        string executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        ClaudeExecutableInfo result = ClaudeExecutableLocator.Resolve(executable);

        Assert.Equal(Path.GetFullPath(executable), result.Path);
        Assert.False(result.SignatureValid);
        Assert.Equal("UNTRUSTED_EXECUTABLE", result.FailureReason);
    }

    [Fact]
    public void InvalidConfiguredExecutableIsNotInstalled()
    {
        ClaudeExecutableInfo result = ClaudeExecutableLocator.Resolve("claude\0.exe");

        Assert.Equal("CLAUDE_NOT_INSTALLED", result.FailureReason);
    }

    [Theory]
    [InlineData(@"\\server\share\claude.exe")]
    [InlineData(@"\\?\UNC\server\share\claude.exe")]
    public void NetworkExecutablePathsAreRejected(string path) =>
        Assert.True(ClaudeExecutableLocator.IsNetworkPath(path));

    [Fact]
    public void WorkspaceSettingsContainOnlyPrivateStatusLineAndAreDeleted()
    {
        string root = Path.Combine(Path.GetTempPath(), "AiUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        string bridge = Path.Combine(root, "claude-statusline-bridge.ps1");
        Directory.CreateDirectory(root);
        File.WriteAllText(bridge, "# bridge");
        string? settingsPath = null;
        try
        {
            var provisioner = new ClaudeWorkspaceProvisioner(new WindowsAppPathProvider(root));
            string workspace = provisioner.EnsureWorkspace();
            Assert.Empty(Directory.EnumerateFileSystemEntries(workspace));

            using (TemporaryClaudeSettings settings =
                   provisioner.CreateSettings(bridge, "AiUsageMonitor-Claude-active-abc123"))
            {
                settingsPath = settings.Path;
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(settings.Path));
                JsonElement rootElement = document.RootElement;
                Assert.Single(rootElement.EnumerateObject());
                string command = rootElement.GetProperty("statusLine").GetProperty("command").GetString()!;
                Assert.Contains(bridge, command, StringComparison.Ordinal);
                Assert.Contains("AiUsageMonitor-Claude-active-abc123", command, StringComparison.Ordinal);
            }
            Assert.False(File.Exists(settingsPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HelperRejectsArbitraryOperationWithoutAttaching()
    {
        using var output = new StringWriter();
        Assert.True(ClaudeConsoleHelper.TryHandle(
            [ClaudeConsoleHelper.Marker, "arbitrary", "1"],
            output));
        ConsoleHelperResponse result = JsonSerializer.Deserialize<ConsoleHelperResponse>(
            output.ToString(),
            SerializerOptions)!;
        Assert.False(result.Ok);
        Assert.Equal("INVALID_OPERATION", result.Reason);
    }

    [Theory]
    [InlineData(-1, false, 0)]
    [InlineData(0, false, 0)]
    [InlineData(120, true, 120)]
    [InlineData(400, true, 400)]
    [InlineData(401, true, 400)]
    [InlineData(short.MaxValue, true, 400)]
    public void ConsoleReadWidthIsBoundedWithoutRejectingWideBuffers(
        int bufferWidth,
        bool expectedSuccess,
        int expectedWidth)
    {
        bool success = ClaudeConsoleHelper.TryGetReadWidth(bufferWidth, out int width);

        Assert.Equal(expectedSuccess, success);
        Assert.Equal(expectedWidth, width);
    }

    [Theory]
    [InlineData(new string[] { "このフォルダーを信頼しますか？" }, ClaudeCliScreenSignature.TrustPrompt)]
    [InlineData(new string[] { "ログインしてください" }, ClaudeCliScreenSignature.SignedOut)]
    [InlineData(new string[] { "テキストスタイルを選択" }, ClaudeCliScreenSignature.SetupScreen)]
    [InlineData(new string[] { "│ >", "ショートカットを表示" }, ClaudeCliScreenSignature.Ready)]
    public void JapaneseSafetySignaturesAreClassified(
        string[] lines,
        ClaudeCliScreenSignature expected) =>
        Assert.Equal(expected, ClaudeCliScreenStateMachine.Classify(lines));

    [WindowsFact]
    public async Task CapabilityProbeAcceptsRequiredFlagsAndVersion()
    {
        ClaudeCliCapabilities result = await ClaudeCliCapabilityProbe.ProbeAsync(
            FindFakeCli(),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.True(result.Supported);
        Assert.Equal("2.1.218 (fake)", result.Version);
        Assert.Null(result.FailureReason);
    }

    [WindowsFact]
    public async Task CapabilityProbeReportsLaunchFailure()
    {
        ClaudeCliCapabilities result = await ClaudeCliCapabilityProbe.ProbeAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe"),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.False(result.Supported);
        Assert.Equal("CAPABILITY_FAILED", result.FailureReason);
    }

    [Theory]
    [InlineData("capability-missing", "REQUIRED_FLAG_MISSING")]
    [InlineData("capability-oversized", "CAPABILITY_FAILED")]
    [InlineData("capability-hang", "CAPABILITY_TIMEOUT")]
    public async Task CapabilityProbeFailsClosedForUnsupportedOrUnboundedCli(
        string variantName,
        string expectedReason)
    {
        string variant = CreateFakeCliVariant(variantName);
        try
        {
            ClaudeCliCapabilities result = await ClaudeCliCapabilityProbe.ProbeAsync(
                variant,
                TimeSpan.FromMilliseconds(500),
                CancellationToken.None);

            Assert.False(result.Supported);
            Assert.Equal(expectedReason, result.FailureReason);
        }
        finally
        {
            await DeleteFileWithRetryAsync(variant);
        }
    }

    [Theory]
    [InlineData("helper-invalid", "HELPER_INVALID_RESPONSE")]
    [InlineData("helper-hang", "HELPER_TIMEOUT")]
    public async Task HelperClientRejectsInvalidOrUnboundedHelperOutput(
        string variantName,
        string expectedReason)
    {
        string variant = CreateFakeHelperHostVariant(variantName);
        try
        {
            var client = new ConsoleHelperClient(variant);
            ConsoleHelperResponse result = await client.ReadAsync(
                1,
                TimeSpan.FromMilliseconds(500),
                CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal(expectedReason, result.Reason);
        }
        finally
        {
            await DeleteFileWithRetryAsync(variant);
        }
    }

    [Fact]
    public async Task HelperClientAcceptsResponseAtExactCharacterLimit()
    {
        string variant = CreateFakeHelperHostVariant("helper-at-limit");
        try
        {
            var client = new ConsoleHelperClient(variant);
            ConsoleHelperResponse result = await client.ReadAsync(
                1,
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

            Assert.True(result.Ok);
        }
        finally
        {
            await DeleteFileWithRetryAsync(variant);
        }
    }

    [Fact]
    public async Task HelperClientRejectsResponseOneCharacterOverLimit()
    {
        string variant = CreateFakeHelperHostVariant("helper-oversized");
        try
        {
            var client = new ConsoleHelperClient(variant);
            ConsoleHelperResponse result = await client.ReadAsync(
                1,
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal("HELPER_INVALID_RESPONSE", result.Reason);
        }
        finally
        {
            await DeleteFileWithRetryAsync(variant);
        }
    }

    private static string FindFakeCli() => AiUsageMonitor.TestSupport.FakeExecutableLocator.FindClaudeFakeCli();

    private static string FindFakeHelperHost() =>
        AiUsageMonitor.TestSupport.FakeExecutableLocator.FindClaudeFakeConsoleHelperHost();

    /// <summary>CLI本体の異常系（--help / --version）を再現する変種を作る。</summary>
    private static string CreateFakeCliVariant(string name) => CreateVariantOf(FindFakeCli(), name);

    /// <summary>console helper protocolの異常系を再現する変種を作る。</summary>
    private static string CreateFakeHelperHostVariant(string name) => CreateVariantOf(FindFakeHelperHost(), name);

    private static string CreateVariantOf(string source, string name)
    {
        // 変種の挙動は実行ファイル名で切り替わるため、名前だけを変えて同じ場所へ複製する。
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
            catch (UnauthorizedAccessException) when (attempt < 19)
            {
                await Task.Delay(100);
            }
        }
    }
}
