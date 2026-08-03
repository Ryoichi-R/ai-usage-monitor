using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;
using AiUsageMonitor.Codex.Runtime;
using AiUsageMonitor.Claude.Usage;

namespace AiUsageMonitor.App;

public sealed class UsageRowViewModel
{
    public required string Label { get; init; }
    public required string Value { get; init; }
    public required string Secondary { get; init; }
    public required double Fraction { get; init; }
    public required UsageSeverity Severity { get; init; }
}

public enum ProviderStatusKind
{
    None,
    Actionable,
    OptionalDataUnavailable,
}

public sealed class MonetaryCardViewModel : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _value = string.Empty;
    private string _secondary = string.Empty;
    private Visibility _visibility = Visibility.Collapsed;

    public string Title { get => _title; set { _title = value; OnPropertyChanged(); } }
    public string Value { get => _value; set { _value = value; OnPropertyChanged(); } }
    public string Secondary { get => _secondary; set { _secondary = value; OnPropertyChanged(); } }
    public Visibility Visibility { get => _visibility; set { _visibility = value; OnPropertyChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new(name));
}

public sealed class UsageViewModel : INotifyPropertyChanged
{
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeZoneInfo _timeZone;

    public ProviderUsageViewModel Codex { get; }
    public ObservableCollection<CodexAccountViewModel> CodexAccounts { get; } = [];
    public ProviderUsageViewModel Claude { get; }
    private bool _showCodex = true;
    private bool _showClaude;
    private IReadOnlyList<string> _codexAccountOrder = [CodexAccountSettings.DefaultAccountId];
    private readonly HashSet<string> _desiredCodexAccounts =
        new(StringComparer.OrdinalIgnoreCase) { CodexAccountSettings.DefaultAccountId };
    public bool ShowCodex { get => _showCodex; set { _showCodex = value; OnPropertyChanged(); OnPropertyChanged(nameof(CodexVisibility)); } }
    public bool ShowClaude { get => _showClaude; set { _showClaude = value; OnPropertyChanged(); OnPropertyChanged(nameof(ClaudeVisibility)); } }
    public Visibility CodexVisibility => ShowCodex ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ClaudeVisibility => ShowClaude ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;

    public UsageViewModel(
        Func<DateTimeOffset>? clock = null,
        TimeZoneInfo? timeZone = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
        _timeZone = timeZone ??
            TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
        Codex = new("CODEX", _clock, _timeZone);
        Claude = new("CLAUDE", _clock, _timeZone);
        CodexAccounts.Add(new(CodexAccountSettings.DefaultAccountId, "CODEX", Codex));
    }

    public void Apply(UsageSnapshot snapshot) =>
        (snapshot.Provider == UsageProvider.Codex ? Codex : Claude).Apply(snapshot);

    internal void ApplyClaude(ClaudeRuntimeSnapshot snapshot, bool setupCompleted)
    {
        UsageSnapshot display = snapshot.Snapshot;
        Claude.Apply(
            display,
            snapshot.Selection.FreshnessKind,
            snapshot.Selection.IsReference,
            snapshot.Selection.ActiveFailureReason);
    }

    public void Apply(CodexAccountSnapshot accountSnapshot)
    {
        if (!accountSnapshot.ShowInWidget ||
            !_desiredCodexAccounts.Contains(accountSnapshot.AccountId))
        {
            CodexAccountViewModel? removed = CodexAccounts.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Id,
                    accountSnapshot.AccountId,
                    StringComparison.OrdinalIgnoreCase));
            if (removed is not null)
                CodexAccounts.Remove(removed);
            return;
        }

        CodexAccountViewModel? account = CodexAccounts.FirstOrDefault(
            candidate => string.Equals(candidate.Id, accountSnapshot.AccountId, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            account = new(
                accountSnapshot.AccountId,
                accountSnapshot.DisplayName,
                new(accountSnapshot.DisplayName, _clock, _timeZone));
            int desiredIndex = _codexAccountOrder
                .TakeWhile(id => !string.Equals(
                    id,
                    accountSnapshot.AccountId,
                    StringComparison.OrdinalIgnoreCase))
                .Count(id => CodexAccounts.Any(existing => string.Equals(
                    existing.Id,
                    id,
                    StringComparison.OrdinalIgnoreCase)));
            CodexAccounts.Insert(Math.Min(desiredIndex, CodexAccounts.Count), account);
        }
        account.DisplayName = accountSnapshot.DisplayName;
        account.Visibility = Visibility.Visible;
        account.Usage.Apply(accountSnapshot.Snapshot);
    }

    public void SynchronizeCodexAccounts(
        IReadOnlyList<CodexAccountSettings> accounts,
        bool showRateLimits,
        bool showAdditionalUsage,
        bool showCredits)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        CodexAccountSettings[] visible = accounts.Where(account => account.ShowInWidget).ToArray();
        _codexAccountOrder = visible.Select(account => account.Id).ToArray();
        _desiredCodexAccounts.Clear();
        foreach (CodexAccountSettings account in visible)
            _desiredCodexAccounts.Add(account.Id);

        foreach (CodexAccountViewModel stale in CodexAccounts.Where(
            current => !_desiredCodexAccounts.Contains(current.Id)).ToArray())
            CodexAccounts.Remove(stale);

        for (int index = 0; index < visible.Length; index++)
        {
            CodexAccountSettings settings = visible[index];
            CodexAccountViewModel? account = CodexAccounts.FirstOrDefault(
                current => string.Equals(current.Id, settings.Id, StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                ProviderUsageViewModel usage = string.Equals(
                    settings.Id,
                    CodexAccountSettings.DefaultAccountId,
                    StringComparison.OrdinalIgnoreCase)
                    ? Codex
                    : new(settings.DisplayName, _clock, _timeZone);
                account = new(settings.Id, settings.DisplayName, usage);
                CodexAccounts.Insert(Math.Min(index, CodexAccounts.Count), account);
            }
            else
            {
                int currentIndex = CodexAccounts.IndexOf(account);
                if (currentIndex != index)
                    CodexAccounts.Move(currentIndex, index);
            }

            account.DisplayName = settings.DisplayName;
            account.Visibility = Visibility.Visible;
            account.Usage.ShowRateLimits = showRateLimits;
            account.Usage.ShowAdditionalUsage = showAdditionalUsage;
            account.Usage.ShowCredits = showCredits;
            if (!settings.Enabled)
            {
                DateTimeOffset now = _clock();
                account.Usage.Apply(new(
                    UsageProvider.Codex,
                    now,
                    now,
                    UsageAvailability.Unavailable,
                    "MONITORING_DISABLED",
                    null,
                    [],
                    null,
                    false,
                    null));
            }
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class CodexAccountViewModel : INotifyPropertyChanged
{
    private string _displayName;
    private Visibility _visibility = Visibility.Visible;

    public CodexAccountViewModel(string id, string displayName, ProviderUsageViewModel usage)
    {
        Id = id;
        _displayName = displayName;
        Usage = usage;
    }

    public string Id { get; }
    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; PropertyChanged?.Invoke(this, new(nameof(DisplayName))); }
    }
    public Visibility Visibility
    {
        get => _visibility;
        set { _visibility = value; PropertyChanged?.Invoke(this, new(nameof(Visibility))); }
    }
    public ProviderUsageViewModel Usage { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ProviderUsageViewModel : INotifyPropertyChanged
{
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeZoneInfo _timeZone;

    public ProviderUsageViewModel(
        string name,
        Func<DateTimeOffset>? clock = null,
        TimeZoneInfo? timeZone = null)
    {
        Name = name;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _timeZone = timeZone ?? TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
    }
    public string Name { get; }
    public ObservableCollection<UsageRowViewModel> Rows { get; } = [];
    public MonetaryCardViewModel AdditionalUsage { get; } = new();
    public MonetaryCardViewModel CreditBalance { get; } = new();
    public bool ShowRateLimits { get; set; } = true;
    public bool ShowAdditionalUsage { get; set; }
    public bool ShowCredits { get; set; }
    private string _statusText = "LOADING…";
    private ProviderStatusKind _statusKind;
    private string _freshnessText = string.Empty;
    private string _freshnessToolTip = string.Empty;
    private UsageSeverity _freshnessSeverity = UsageSeverity.Normal;
    public string StatusText => _statusText;
    public ProviderStatusKind StatusKind => _statusKind;
    public Visibility StatusVisibility => string.IsNullOrEmpty(StatusText) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CompactStatusVisibility =>
        StatusVisibility == Visibility.Visible && StatusKind != ProviderStatusKind.OptionalDataUnavailable
            ? Visibility.Visible
            : Visibility.Collapsed;
    public string FreshnessText
    {
        get => _freshnessText;
        private set
        {
            _freshnessText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FreshnessVisibility));
            OnPropertyChanged(nameof(FreshnessCompactToolTip));
        }
    }

    public string FreshnessToolTip
    {
        get => _freshnessToolTip;
        private set
        {
            _freshnessToolTip = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FreshnessCompactToolTip));
        }
    }

    /// <summary>Compact 表示で省略された freshness を復元できる全文ツールチップ。</summary>
    public string FreshnessCompactToolTip => string.IsNullOrWhiteSpace(FreshnessToolTip)
        ? FreshnessText
        : string.IsNullOrWhiteSpace(FreshnessText)
            ? FreshnessToolTip
            : $"{FreshnessText}\n{FreshnessToolTip}";
    public Visibility FreshnessVisibility =>
        string.IsNullOrEmpty(FreshnessText)
            ? Visibility.Collapsed
            : Visibility.Visible;
    public UsageSeverity FreshnessSeverity { get => _freshnessSeverity; private set { _freshnessSeverity = value; OnPropertyChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(UsageSnapshot snapshot) =>
        Apply(snapshot, ClaudeUsageFreshnessKind.None, false, null);

    public void Apply(
        UsageSnapshot snapshot,
        ClaudeUsageFreshnessKind freshnessKind,
        bool isReference,
        string? activeFailureReason)
    {
        Rows.Clear();
        // TTL内のrefresh失敗では前回値を維持し、TTL超過・reset通過（Stale）では割合とバーを消す。
        // 古い割合を「現在値」として残さないという契約はTTLで担保する。
        bool showRows = !snapshot.IsStale && snapshot.Availability != UsageAvailability.Stale;
        UsageWindowSnapshot[] visibleWindows =
            (showRows && ShowRateLimits ? snapshot.Windows : []).ToArray();
        CodexIndividualLimitSnapshot? individual = showRows ? snapshot.IndividualLimit : null;
        AdditionalUsage.Visibility = ShowAdditionalUsage && individual is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (individual is not null)
        {
            AdditionalUsage.Title = "追加利用額";
            AdditionalUsage.Value =
                CodexMonetaryFormatter.FormatIndividualLimitValue(individual);
            AdditionalUsage.Secondary =
                CodexMonetaryFormatter.FormatIndividualLimitSecondary(
                    individual,
                    TimeZoneInfo.Local);
        }
        CodexCreditSnapshot? credit = showRows ? snapshot.CreditSnapshot : null;
        CreditBalance.Visibility = ShowCredits &&
            CodexMonetaryFormatter.ShouldDisplayCredit(credit)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (credit is not null)
        {
            CreditBalance.Title = "クレジット残高";
            CreditBalance.Value = CodexMonetaryFormatter.FormatCreditValue(credit);
            CreditBalance.Secondary = credit.IsUnlimited ? string.Empty : "現在の残高";
        }
        (string statusText, ProviderStatusKind statusKind) = isReference
            ? activeFailureReason is null
                ? ("statusLine参考値 — 最新性未保証", ProviderStatusKind.Actionable)
                : ($"statusLine参考値 — 最新性未保証 / 公式CLI更新失敗: {activeFailureReason}", ProviderStatusKind.Actionable)
            : StatusFor(snapshot);
        SetStatus(statusText, statusKind);
        string? freshPrefix = freshnessKind switch
        {
            ClaudeUsageFreshnessKind.Cli => "CLI",
            ClaudeUsageFreshnessKind.StatusLineReceipt => "SL受信",
            _ => null,
        };
        string? retainedPrefix = freshnessKind switch
        {
            ClaudeUsageFreshnessKind.Cli => "CLI最終",
            ClaudeUsageFreshnessKind.StatusLineReceipt => "SL最終",
            _ => null,
        };
        UsageFreshnessDisplay? freshness = UsageFreshnessFormatter.Format(
            snapshot,
            _clock(),
            _timeZone,
            freshPrefix,
            retainedPrefix);
        FreshnessText = freshness?.Text ?? string.Empty;
        FreshnessToolTip = freshnessKind switch
        {
            ClaudeUsageFreshnessKind.Cli =>
                "CLI: 公式 /usage 画面の読取り完了時刻（server measurement timestampではありません）",
            ClaudeUsageFreshnessKind.StatusLineReceipt =>
                "SL受信: statusLineのpipe受信時刻（server measurement timestampではありません）",
            _ => string.Empty,
        };
        FreshnessSeverity = freshness?.Severity ?? UsageSeverity.Normal;
        foreach (UsageWindowSnapshot window in visibleWindows)
        {
            string state = window.IsLimitReached ? " LIMIT" : window.RemainingPercent <= 20 ? " LOW" : string.Empty;
            Rows.Add(new UsageRowViewModel
            {
                Label = UsageWindowFormatter.Label(window),
                Value = UsageWindowFormatter.Remaining(window),
                Secondary = UsageWindowFormatter.Reset(window, _clock(), _timeZone) + state,
                Fraction = window.RemainingPercent / 100d,
                Severity = window.RemainingPercent <= 10
                    ? UsageSeverity.Danger
                    : window.RemainingPercent <= 20
                        ? UsageSeverity.Warning
                        : UsageSeverity.Accent,
            });
        }
    }

    private (string Text, ProviderStatusKind Kind) StatusFor(UsageSnapshot snapshot)
    {
        if (string.Equals(snapshot.Reason, "STATUSLINE_WAITING", StringComparison.Ordinal))
            return ("statusLine待機中", ProviderStatusKind.Actionable);
        if (snapshot.Reason?.StartsWith("STATUSLINE_NOT_RECEIVED:", StringComparison.Ordinal) == true)
        {
            string minutes = snapshot.Reason["STATUSLINE_NOT_RECEIVED:".Length..];
            return ($"statusLine未受信 {minutes}分 — Claude Code sessionとstatusLine設定を確認", ProviderStatusKind.Actionable);
        }
        if (snapshot.Availability != UsageAvailability.Available)
            return (UsageStatusFormatter.Format(Name, snapshot), ProviderStatusKind.Actionable);
        if (snapshot.Provider == UsageProvider.Claude &&
            !string.IsNullOrWhiteSpace(snapshot.Reason))
            return ("前回値を表示中 — 更新に失敗しました", ProviderStatusKind.Actionable);
        if (ShowRateLimits &&
            snapshot.RateLimitAvailability == UsageAvailability.Unsupported)
            return (UsageStatusFormatter.Format(
                Name,
                snapshot with { Reason = snapshot.RateLimitReason ?? "SCHEMA_UNSUPPORTED" }),
                ProviderStatusKind.Actionable);

        bool optionalSelected = ShowAdditionalUsage || ShowCredits;
        bool optionalVisible = AdditionalUsage.Visibility == Visibility.Visible ||
            CreditBalance.Visibility == Visibility.Visible;
        return optionalSelected && !optionalVisible
            ? ("利用可能な追加情報はありません", ProviderStatusKind.OptionalDataUnavailable)
            : (string.Empty, ProviderStatusKind.None);
    }

    private void SetStatus(string text, ProviderStatusKind kind)
    {
        _statusText = text;
        _statusKind = kind;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusKind));
        OnPropertyChanged(nameof(StatusVisibility));
        OnPropertyChanged(nameof(CompactStatusVisibility));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
