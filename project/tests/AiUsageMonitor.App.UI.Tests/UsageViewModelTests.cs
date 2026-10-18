using AiUsageMonitor.Codex.Runtime;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Claude.Usage;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;

using AiUsageMonitor.App.UI.ViewModels;

namespace AiUsageMonitor.App.UI.Tests;

public sealed class UsageViewModelTests
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
        Assert.True(usage.CreditBalance.IsVisible);
        Assert.Equal("$0.00", usage.CreditBalance.Value);
        Assert.Equal("現在の残高", usage.CreditBalance.Secondary);

        usage.Apply(AvailableSnapshot(new(false, true, null)));
        Assert.True(usage.CreditBalance.IsVisible);
        Assert.Equal("無制限", usage.CreditBalance.Value);
        Assert.Empty(usage.CreditBalance.Secondary);

        usage.Apply(AvailableSnapshot(new(true, false, 0.004m)));
        Assert.Equal("<$0.01", usage.CreditBalance.Value);

        usage.Apply(AvailableSnapshot(new(false, false, 1)));
        Assert.False(usage.CreditBalance.IsVisible);
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
        Assert.True(usage.CreditBalance.IsVisible);
        Assert.True(usage.AdditionalUsage.IsVisible);

        UsageSnapshot replacement = AvailableSnapshot() with
        {
            RateLimitAvailability = UsageAvailability.Unsupported,
            RateLimitReason = "SCHEMA_UNSUPPORTED",
        };
        usage.Apply(replacement);

        Assert.False(usage.CreditBalance.IsVisible);
        Assert.False(usage.AdditionalUsage.IsVisible);
        Assert.Equal("使用上限の形式に対応していません", usage.StatusText);
        Assert.Equal(ProviderStatusKind.Actionable, usage.StatusKind);
        Assert.True(usage.IsCompactStatusVisible);
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
        Assert.False(usage.IsCompactStatusVisible);
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
        Assert.True(usage.IsCompactStatusVisible);
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
        Assert.False(usage.IsFreshnessVisible);
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
        Assert.True(usage.IsFreshnessVisible);
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
        Assert.True(usage.IsFreshnessVisible);
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

        Assert.True(usage.CreditBalance.IsVisible);
        Assert.Single(usage.Rows);
        Assert.True(usage.IsFreshnessVisible);
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

        Assert.False(usage.IsFreshnessVisible);
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

    [Fact]
    public void HiddenAccountReturnsAtItsSettingsPositionAndReorderFollowsSettings()
    {
        var viewModel = new UsageViewModel(() => FixedNow, Tokyo);
        CodexAccountSettings[] accounts =
        [
            new() { Id = "a", DisplayName = "A", CodexHomePath = "/a" },
            new() { Id = "b", DisplayName = "B", CodexHomePath = "/b" },
            new() { Id = "c", DisplayName = "C", CodexHomePath = "/c" },
        ];
        viewModel.SynchronizeCodexAccounts(accounts, true, false, false);

        viewModel.Apply(new CodexAccountSnapshot("b", "B", false, AvailableSnapshot()));
        Assert.Equal(["a", "c"], viewModel.CodexAccounts.Select(account => account.Id));

        viewModel.Apply(new CodexAccountSnapshot("b", "B renamed", true, AvailableSnapshot()));
        Assert.Equal(["a", "b", "c"], viewModel.CodexAccounts.Select(account => account.Id));
        Assert.Equal("B renamed", viewModel.CodexAccounts[1].DisplayName);
        Assert.True(viewModel.CodexAccounts[1].IsVisible);

        viewModel.SynchronizeCodexAccounts([accounts[2], accounts[0], accounts[1]], true, false, false);
        Assert.Equal(["c", "a", "b"], viewModel.CodexAccounts.Select(account => account.Id));
    }

    [Fact]
    public void ApplyClaudeUsesSelectionFreshnessAndReferenceState()
    {
        var viewModel = new UsageViewModel(() => FixedNow, Tokyo) { ShowClaude = true };
        UsageSnapshot passive = Snapshot(
            UsageProvider.Claude,
            FixedNow.AddMinutes(-2),
            FixedNow.AddMinutes(-2));
        var selection = new ClaudeUsageSelection(
            passive,
            null,
            true,
            "TEST",
            ClaudeUsageFreshnessKind.StatusLineReceipt,
            null,
            "TIMEOUT",
            null);

        viewModel.ApplyClaude(new Runtime.ClaudeRuntimeSnapshot(1, passive, selection));

        Assert.Equal("SL受信 14:28", viewModel.Claude.FreshnessText);
        Assert.Contains("TIMEOUT", viewModel.Claude.StatusText, StringComparison.Ordinal);
        Assert.True(viewModel.Claude.IsCompactStatusVisible);
    }

    [Fact]
    public void LegacyRuntimeSnapshotHasNoFreshnessProvenance()
    {
        UsageSnapshot snapshot = Snapshot(
            UsageProvider.Claude,
            FixedNow.AddMinutes(-1),
            FixedNow.AddMinutes(-1));

        var runtime = new Runtime.ClaudeRuntimeSnapshot(7, snapshot);

        Assert.Equal(7, runtime.Generation);
        Assert.Same(snapshot, runtime.Snapshot);
        Assert.Equal(ClaudeUsageFreshnessKind.None, runtime.Selection.FreshnessKind);
        Assert.False(runtime.Selection.IsReference);
    }

    [Fact]
    public void VisibilityPropertiesRaiseChangeNotificationsUnderTheirBoolNames()
    {
        var viewModel = new UsageViewModel(() => FixedNow, Tokyo);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        viewModel.ShowCodex = false;
        viewModel.ShowClaude = true;
        Assert.Equal([nameof(UsageViewModel.ShowCodex), nameof(UsageViewModel.ShowClaude)], changed);

        var usage = new ProviderUsageViewModel("CLAUDE", () => FixedNow, Tokyo);
        var usageChanged = new List<string?>();
        usage.PropertyChanged += (_, e) => usageChanged.Add(e.PropertyName);
        usage.Apply(Snapshot(UsageProvider.Claude, FixedNow.AddMinutes(-1), FixedNow.AddMinutes(-1)));
        Assert.Contains(nameof(ProviderUsageViewModel.IsStatusVisible), usageChanged);
        Assert.Contains(nameof(ProviderUsageViewModel.IsCompactStatusVisible), usageChanged);
        Assert.Contains(nameof(ProviderUsageViewModel.IsFreshnessVisible), usageChanged);
        Assert.False(usage.IsStatusVisible);

        var account = new CodexAccountViewModel("a", "A", usage);
        var accountChanged = new List<string?>();
        account.PropertyChanged += (_, e) => accountChanged.Add(e.PropertyName);
        account.IsVisible = false;
        account.DisplayName = "B";
        Assert.Equal([nameof(CodexAccountViewModel.IsVisible), nameof(CodexAccountViewModel.DisplayName)], accountChanged);

        var card = new MonetaryCardViewModel();
        var cardChanged = new List<string?>();
        card.PropertyChanged += (_, e) => cardChanged.Add(e.PropertyName);
        card.IsVisible = true;
        Assert.Equal([nameof(MonetaryCardViewModel.IsVisible)], cardChanged);
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
