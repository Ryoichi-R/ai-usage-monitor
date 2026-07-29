using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Core.Tests;

public sealed class UsageStatusFormatterTests
{
    [Theory]
    [InlineData("CODEX_HOME_UNAVAILABLE", "Codexホームを利用できません")]
    [InlineData("CODEX_HOME_DUPLICATE", "同じCodexホームが別の設定で使われています")]
    [InlineData("DUPLICATE_ACCOUNT", "同じアカウントが別のCodexホームで使われています")]
    [InlineData("MONITORING_DISABLED", "監視を停止しています")]
    [InlineData("SIGNED_OUT", "Codexへサインインしてください")]
    [InlineData("CODEX_NOT_FOUND", "Codexが見つかりません")]
    [InlineData("METHOD_UNSUPPORTED", "このCodexでは利用情報を取得できません")]
    [InlineData("SCHEMA_UNSUPPORTED", "使用上限の形式に対応していません")]
    [InlineData("RPC_TIMEOUT", "利用情報を取得できません")]
    [InlineData("RPC_FAILURE", "利用情報を取得できません")]
    public void FormatsCodexReasonsWithoutProviderName(string reason, string expected)
    {
        UsageSnapshot snapshot = Snapshot(UsageProvider.Codex, UsageAvailability.Unavailable, reason);
        Assert.Equal(expected, UsageStatusFormatter.Format("CODEX", snapshot));
    }

    [Fact]
    public void StaleTransportIsReasonFirst()
    {
        UsageSnapshot snapshot = Snapshot(
            UsageProvider.Codex,
            UsageAvailability.Stale,
            "RPC_FAILURE") with
        { IsStale = true };
        Assert.Equal("更新が停止しています", UsageStatusFormatter.Format("CODEX", snapshot));
    }

    [Fact]
    public void FormatsClaudeConnectionStates()
    {
        Assert.Equal(
            "Claude Codeと接続しました。",
            UsageStatusFormatter.FormatClaudeConnection(
                Snapshot(UsageProvider.Claude, UsageAvailability.Available, null)));
        Assert.Contains(
            "サインイン",
            UsageStatusFormatter.FormatClaudeConnection(
                Snapshot(UsageProvider.Claude, UsageAvailability.SignedOut, null)));
    }

    [Theory]
    [InlineData(UsageAvailability.Loading, "接続を確認しています")]
    [InlineData(UsageAvailability.NotInstalled, "Codexが見つかりません")]
    [InlineData(UsageAvailability.SignedOut, "ChatGPTへサインインしてください")]
    [InlineData(UsageAvailability.Unsupported, "このCodexでは利用情報を取得できません")]
    [InlineData(UsageAvailability.Unavailable, "利用情報を取得できません")]
    [InlineData(UsageAvailability.Error, "利用情報を取得できません")]
    [InlineData(UsageAvailability.Available, "")]
    public void FormatsCodexAvailabilityFallback(
        UsageAvailability availability,
        string expected)
    {
        Assert.Equal(
            expected,
            UsageStatusFormatter.Format(
                "CODEX",
                Snapshot(UsageProvider.Codex, availability, null)));
    }

    [Theory]
    [InlineData(UsageAvailability.Setup)]
    [InlineData(UsageAvailability.Loading)]
    [InlineData(UsageAvailability.Waiting)]
    [InlineData(UsageAvailability.Unavailable)]
    [InlineData(UsageAvailability.NotInstalled)]
    [InlineData(UsageAvailability.SignedOut)]
    [InlineData(UsageAvailability.Unsupported)]
    [InlineData(UsageAvailability.Error)]
    public void FormatsEveryClaudeStatus(UsageAvailability availability)
    {
        string result = UsageStatusFormatter.Format(
            "CLAUDE",
            Snapshot(UsageProvider.Claude, availability, null));
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Theory]
    [InlineData(UsageAvailability.Setup)]
    [InlineData(UsageAvailability.Loading)]
    [InlineData(UsageAvailability.Waiting)]
    [InlineData(UsageAvailability.Unavailable)]
    [InlineData(UsageAvailability.NotInstalled)]
    [InlineData(UsageAvailability.Unsupported)]
    [InlineData(UsageAvailability.Error)]
    public void FormatsEveryClaudeConnectionStatus(UsageAvailability availability)
    {
        string result = UsageStatusFormatter.FormatClaudeConnection(
            Snapshot(UsageProvider.Claude, availability, null));
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public void UnknownStaleCodexStateUsesGenericStoppedMessage()
    {
        UsageSnapshot snapshot = Snapshot(
            UsageProvider.Codex,
            UsageAvailability.Stale,
            "UNKNOWN") with
        { IsStale = true };
        Assert.Equal("更新が停止しています", UsageStatusFormatter.Format("CODEX", snapshot));
    }

    private static UsageSnapshot Snapshot(
        UsageProvider provider,
        UsageAvailability availability,
        string? reason)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(provider, now, now, availability, reason, null, [], null, false, null);
    }
}
