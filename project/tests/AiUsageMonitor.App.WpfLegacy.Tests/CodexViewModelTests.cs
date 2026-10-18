using System.Windows;
using AiUsageMonitor.Codex.Runtime;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Claude.Usage;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.App.Tests;

public sealed class CodexViewModelTests
{
    private static readonly TimeZoneInfo Tokyo =
        TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
    private static readonly DateTimeOffset FixedNow =
        new(2026, 7, 26, 14, 30, 0, TimeSpan.FromHours(9));

    [Fact]
    public void AccountCollectionFollowsSettingsOrderAndRemovesDeletedAccounts()
    {
        var viewModel = new UsageViewModel();
        CodexAccountSettings[] initial =
        [
            new() { Id = "b", DisplayName = "B", CodexHomePath = @"C:\b" },
            new() { Id = "a", DisplayName = "A", CodexHomePath = @"C:\a" },
        ];

        viewModel.SynchronizeCodexAccounts(initial, true, true, true);
        Assert.Equal(["b", "a"], viewModel.CodexAccounts.Select(account => account.Id));

        viewModel.SynchronizeCodexAccounts(
            [initial[1] with { Enabled = false }],
            true,
            true,
            true);

        CodexAccountViewModel remaining = Assert.Single(viewModel.CodexAccounts);
        Assert.Equal("a", remaining.Id);
        Assert.Equal("監視を停止しています", remaining.Usage.StatusText);
    }

    [Fact]
    public void HiddenSnapshotCannotRecreateRemovedAccount()
    {
        var viewModel = new UsageViewModel();
        viewModel.SynchronizeCodexAccounts([], true, false, false);

        viewModel.Apply(new CodexAccountSnapshot(
            "removed",
            "Removed",
            false,
            AvailableSnapshot()));

        Assert.Empty(viewModel.CodexAccounts);
    }

    [Fact]
    public void CreditCardFollowsZeroUnlimitedAndSmallBalanceRules()
    {
        var usage = new ProviderUsageViewModel("CODEX") { ShowCredits = true };

        usage.Apply(AvailableSnapshot(new(false, false, 0)));
        Assert.Equal(Visibility.Visible, usage.CreditBalance.Visibility);
        Assert.Equal("$0.00", usage.CreditBalance.Value);
        Assert.Equal("現在の残高", usage.CreditBalance.Secondary);

        usage.Apply(AvailableSnapshot(new(false, true, null)));
        Assert.Equal(Visibility.Visible, usage.CreditBalance.Visibility);
        Assert.Equal("無制限", usage.CreditBalance.Value);
        Assert.Empty(usage.CreditBalance.Secondary);

        usage.Apply(AvailableSnapshot(new(true, false, 0.004m)));
        Assert.Equal("<$0.01", usage.CreditBalance.Value);

        usage.Apply(AvailableSnapshot(new(false, false, 1)));
        Assert.Equal(Visibility.Collapsed, usage.CreditBalance.Visibility);
    }

    [Fact]
    public void SuccessfulResponseReplacesMissingComponentsAndKeepsSchemaStatus()
    {
        var usage = new ProviderUsageViewModel("CODEX")
        {
            ShowRateLimits = true,
            ShowAdditionalUsage = true,
            ShowCredits = true,
        };
        usage.Apply(AvailableSnapshot(
            new(true, false, 1),
            new(2, 10, 80, null)));
        Assert.Equal(Visibility.Visible, usage.CreditBalance.Visibility);
        Assert.Equal(Visibility.Visible, usage.AdditionalUsage.Visibility);

        UsageSnapshot replacement = AvailableSnapshot() with
        {
            RateLimitAvailability = UsageAvailability.Unsupported,
            RateLimitReason = "SCHEMA_UNSUPPORTED",
        };
        usage.Apply(replacement);

        Assert.Equal(Visibility.Collapsed, usage.CreditBalance.Visibility);
        Assert.Equal(Visibility.Collapsed, usage.AdditionalUsage.Visibility);
        Assert.Equal("使用上限の形式に対応していません", usage.StatusText);
        Assert.Equal(ProviderStatusKind.Actionable, usage.StatusKind);
        Assert.Equal(Visibility.Visible, usage.CompactStatusVisibility);
    }

    [Fact]
    public void OptionalSelectionWithoutComponentsShowsShortStatus()
    {
        var usage = new ProviderUsageViewModel("CODEX")
        {
            ShowRateLimits = false,
            ShowAdditionalUsage = true,
            ShowCredits = true,
        };

        usage.Apply(AvailableSnapshot());

        Assert.Equal("利用可能な追加情報はありません", usage.StatusText);
        Assert.Equal(ProviderStatusKind.OptionalDataUnavailable, usage.StatusKind);
        Assert.Equal(Visibility.Collapsed, usage.CompactStatusVisibility);
    }

    [Fact]
    public void AdditionalUsageFormatsResetAndLimitState()
    {
        var usage = new ProviderUsageViewModel("CODEX")
        {
            ShowAdditionalUsage = true,
        };
        usage.Apply(AvailableSnapshot(
            individual: new(
                12,
                10,
                0,
                new DateTimeOffset(2026, 7, 27, 1, 2, 0, TimeSpan.Zero))));

        Assert.Contains("リセット", usage.AdditionalUsage.Secondary, StringComparison.Ordinal);
        Assert.EndsWith(" · LIMIT", usage.AdditionalUsage.Secondary, StringComparison.Ordinal);
    }

    [Fact]
    public void ClaudeStaleSnapshotHidesRowsAndShowsLastAcquiredDanger()
    {
        var usage = new ProviderUsageViewModel(
            "CLAUDE",
            () => FixedNow,
            Tokyo);
        UsageSnapshot fresh = Snapshot(
            UsageProvider.Claude,
            FixedNow.AddMinutes(-5),
            FixedNow.AddMinutes(-5));

        usage.Apply(fresh);
        Assert.Single(usage.Rows);
        Assert.Equal("取得 14:25", usage.FreshnessText);
        Assert.Equal(UsageSeverity.Normal, usage.FreshnessSeverity);

        usage.Apply(fresh with
        {
            Availability = UsageAvailability.Stale,
            Reason = "RESET_PASSED",
            IsStale = true,
            ReceivedAt = FixedNow,
        });

        Assert.Empty(usage.Rows);
        // RESET_PASSEDは5時間枠reset直後の既知の自動回復待ちであり、汎用stale文言ではなく
        // UsageStatusFormatterのstale reason別messageを表示する。
        Assert.Equal("5時間枠の更新待ち — 自動再開します", usage.StatusText);
        Assert.Equal("最終取得 14:25", usage.FreshnessText);
        Assert.Equal(UsageSeverity.Danger, usage.FreshnessSeverity);
    }

    [Fact]
    public void RefreshFailureDoesNotAdvanceDisplayedSuccessfulTime()
    {
        var usage = new ProviderUsageViewModel(
            "CLAUDE",
            () => FixedNow,
            Tokyo);
        UsageSnapshot fresh = Snapshot(
            UsageProvider.Claude,
            FixedNow.AddMinutes(-5),
            FixedNow.AddMinutes(-5));
        usage.Apply(fresh);

        usage.Apply(fresh with
        {
            Availability = UsageAvailability.Available,
            Reason = "USAGE_SCREEN_PARSE_FAILED",
            ReceivedAt = FixedNow,
        });

        Assert.Equal("最終取得 14:25", usage.FreshnessText);
        Assert.Equal(UsageSeverity.Warning, usage.FreshnessSeverity);
    }

    [Fact]
    public void ClaudeReferenceShowsReceiptTooltipAndActiveFailureDiagnostic()
    {
        var usage = new ProviderUsageViewModel(
            "CLAUDE",
            () => FixedNow,
            Tokyo);
        UsageSnapshot passive = Snapshot(
            UsageProvider.Claude,
            FixedNow.AddMinutes(-2),
            FixedNow.AddMinutes(-2));

        usage.Apply(
            passive,
            ClaudeUsageFreshnessKind.StatusLineReceipt,
            isReference: true,
            activeFailureReason: "TIMEOUT");

        Assert.Equal("SL受信 14:28", usage.FreshnessText);
        Assert.Contains("server measurement timestampではありません", usage.FreshnessToolTip);
        Assert.StartsWith("SL受信 14:28\n", usage.FreshnessCompactToolTip, StringComparison.Ordinal);
        Assert.Contains(usage.FreshnessToolTip, usage.FreshnessCompactToolTip, StringComparison.Ordinal);
        Assert.Contains("statusLine参考値 — 最新性未保証", usage.StatusText);
        Assert.Contains("TIMEOUT", usage.StatusText);
        Assert.Equal(ProviderStatusKind.Actionable, usage.StatusKind);
        Assert.Equal(Visibility.Visible, usage.CompactStatusVisibility);
    }

    [Fact]
    public void ClaudeCliFreshnessHasExplicitLocalReadCompletionTooltip()
    {
        var usage = new ProviderUsageViewModel(
            "CLAUDE",
            () => FixedNow,
            Tokyo);

        usage.Apply(
            Snapshot(
                UsageProvider.Claude,
                FixedNow.AddMinutes(-1),
                FixedNow.AddMinutes(-1)),
            ClaudeUsageFreshnessKind.Cli,
            isReference: false,
            activeFailureReason: null);

        Assert.Equal("CLI 14:29", usage.FreshnessText);
        Assert.Contains("/usage 画面の読取り完了時刻", usage.FreshnessToolTip);
        Assert.Contains("server measurement timestampではありません", usage.FreshnessToolTip);
        Assert.StartsWith("CLI 14:29\n", usage.FreshnessCompactToolTip, StringComparison.Ordinal);
        Assert.Contains(usage.FreshnessToolTip, usage.FreshnessCompactToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void CodexCompactFreshnessRetainsFullTextWhenProvenanceIsEmpty()
    {
        var usage = new ProviderUsageViewModel("CODEX", () => FixedNow, Tokyo);

        usage.Apply(Snapshot(
            UsageProvider.Codex,
            FixedNow.AddMinutes(-1),
            FixedNow.AddMinutes(-1)));

        Assert.NotEmpty(usage.FreshnessText);
        Assert.Empty(usage.FreshnessToolTip);
        Assert.Equal(usage.FreshnessText, usage.FreshnessCompactToolTip);
    }

    [Theory]
    [InlineData("STATUSLINE_WAITING", "statusLine待機中")]
    [InlineData(
        "STATUSLINE_NOT_RECEIVED:3",
        "statusLine未受信 3分 — Claude Code sessionとstatusLine設定を確認")]
    public void StatusLineNoReceiptDiagnosticIsVisibleWithoutBars(
        string reason,
        string expected)
    {
        var usage = new ProviderUsageViewModel("CLAUDE");

        usage.Apply(UsageSnapshot.Loading(UsageProvider.Claude, FixedNow) with
        {
            Availability = UsageAvailability.Waiting,
            Reason = reason,
        });

        Assert.Equal(expected, usage.StatusText);
        Assert.Empty(usage.Rows);
        Assert.Equal(Visibility.Collapsed, usage.FreshnessVisibility);
    }

    [Fact]
    public void CodexAccountsAndClaudeDisplayIndependentSuccessfulTimes()
    {
        var viewModel = new UsageViewModel(() => FixedNow, Tokyo)
        {
            ShowCodex = true,
            ShowClaude = true,
        };
        CodexAccountSettings[] accounts =
        [
            new() { Id = "a", DisplayName = "Account A", CodexHomePath = @"C:\a" },
            new() { Id = "b", DisplayName = "Account B", CodexHomePath = @"C:\b" },
        ];
        viewModel.SynchronizeCodexAccounts(accounts, true, false, false);
        viewModel.Apply(new CodexAccountSnapshot(
            "a",
            "Account A",
            true,
            Snapshot(
                UsageProvider.Codex,
                FixedNow.AddMinutes(-1),
                FixedNow.AddMinutes(-1))));
        viewModel.Apply(new CodexAccountSnapshot(
            "b",
            "Account B",
            true,
            Snapshot(
                UsageProvider.Codex,
                FixedNow.AddMinutes(-2),
                FixedNow.AddMinutes(-2))));
        viewModel.Apply(Snapshot(
            UsageProvider.Claude,
            FixedNow.AddMinutes(-3),
            FixedNow.AddMinutes(-3)));

        Assert.Equal("取得 14:29", viewModel.CodexAccounts[0].Usage.FreshnessText);
        Assert.Equal("取得 14:28", viewModel.CodexAccounts[1].Usage.FreshnessText);
        Assert.Equal("取得 14:27", viewModel.Claude.FreshnessText);
    }

    [Fact]
    public void SingleUsageRowWithoutOtherDetailsKeepsFreshnessInProviderHeader()
    {
        var usage = new ProviderUsageViewModel(
            "CODEX",
            () => FixedNow,
            Tokyo);

        usage.Apply(Snapshot(
            UsageProvider.Codex,
            FixedNow.AddMinutes(-5),
            FixedNow.AddMinutes(-5)));

        Assert.Single(usage.Rows);
        Assert.Equal("取得 14:25", usage.FreshnessText);
        Assert.Equal(Visibility.Visible, usage.FreshnessVisibility);
    }

    [Fact]
    public void MultipleUsageRowsKeepFreshnessInProviderHeader()
    {
        var usage = new ProviderUsageViewModel(
            "CODEX",
            () => FixedNow,
            Tokyo);
        UsageSnapshot snapshot = Snapshot(
            UsageProvider.Codex,
            FixedNow.AddMinutes(-5),
            FixedNow.AddMinutes(-5));
        usage.Apply(snapshot with
        {
            Windows =
            [
                .. snapshot.Windows,
                new(
                    null,
                    null,
                    "seven_day",
                    25,
                    UsageWindowPolicy.SevenDayDurationMinutes,
                    FixedNow.AddDays(1),
                    null),
            ],
        });

        Assert.Equal(2, usage.Rows.Count);
        Assert.Equal(Visibility.Visible, usage.FreshnessVisibility);
    }

    [Fact]
    public void SingleUsageRowWithMonetaryDetailKeepsFreshnessInProviderHeader()
    {
        var usage = new ProviderUsageViewModel(
            "CODEX",
            () => FixedNow,
            Tokyo)
        {
            ShowCredits = true,
        };
        usage.Apply(Snapshot(
            UsageProvider.Codex,
            FixedNow.AddMinutes(-5),
            FixedNow.AddMinutes(-5)) with
        {
            Credits = 200,
            CreditSnapshot = new(true, false, 200),
        });

        Assert.Equal(Visibility.Visible, usage.CreditBalance.Visibility);
        Assert.Single(usage.Rows);
        Assert.Equal(Visibility.Visible, usage.FreshnessVisibility);
    }

    [Fact]
    public void FailureWithoutSuccessfulHistoryHidesFreshness()
    {
        var usage = new ProviderUsageViewModel(
            "CLAUDE",
            () => FixedNow,
            Tokyo);
        usage.Apply(Snapshot(
            UsageProvider.Claude,
            FixedNow,
            null) with
        {
            Availability = UsageAvailability.Unsupported,
            Reason = "REQUIRED_FLAG_MISSING",
            Windows = [],
        });

        Assert.Equal(Visibility.Collapsed, usage.FreshnessVisibility);
        Assert.Empty(usage.FreshnessText);
    }

    [Theory]
    [InlineData("CODEX_HOME_UNAVAILABLE", false, "Codexホームを利用できません")]
    [InlineData("CODEX_HOME_DUPLICATE", false, "同じCodexホームが別の設定で使われています")]
    [InlineData("DUPLICATE_ACCOUNT", false, "同じアカウントが別のCodexホームで使われています")]
    [InlineData("MONITORING_DISABLED", false, "監視を停止しています")]
    [InlineData("SIGNED_OUT", false, "Codexへサインインしてください")]
    [InlineData("CODEX_NOT_FOUND", false, "Codexが見つかりません")]
    [InlineData("METHOD_UNSUPPORTED", false, "このCodexでは利用情報を取得できません")]
    [InlineData("SCHEMA_UNSUPPORTED", false, "使用上限の形式に対応していません")]
    [InlineData("RPC_FAILURE", false, "利用情報を取得できません")]
    [InlineData("RPC_FAILURE", true, "更新が停止しています")]
    public void StatusReasonsReachWidgetInJapanese(
        string reason,
        bool stale,
        string expected)
    {
        var usage = new ProviderUsageViewModel("CODEX");
        usage.Apply(AvailableSnapshot() with
        {
            Availability = stale
                ? UsageAvailability.Stale
                : UsageAvailability.Unavailable,
            Reason = reason,
            IsStale = stale,
        });

        Assert.Equal(expected, usage.StatusText);
    }

    private static UsageSnapshot AvailableSnapshot(
        CodexCreditSnapshot? credit = null,
        CodexIndividualLimitSnapshot? individual = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(
            UsageProvider.Codex,
            now,
            now,
            UsageAvailability.Available,
            null,
            null,
            [],
            credit?.Balance,
            false,
            now,
            CreditSnapshot: credit,
            IndividualLimit: individual);
    }

    private static UsageSnapshot Snapshot(
        UsageProvider provider,
        DateTimeOffset receivedAt,
        DateTimeOffset? successfulAt) =>
        new(
            provider,
            receivedAt,
            receivedAt,
            UsageAvailability.Available,
            null,
            null,
            [
                new(
                    null,
                    null,
                    "five_hour",
                    25,
                    UsageWindowPolicy.FiveHourDurationMinutes,
                    FixedNow.AddHours(1),
                    null),
            ],
            null,
            false,
            successfulAt);
}
