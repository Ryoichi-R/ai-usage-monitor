using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Acquisition;

/// <summary>
/// 取得元を跨いで共通の内部観測。生screen、account email、session ID、cwd、prompt、transcriptを保持しない。
/// </summary>
public sealed record ClaudeUsageObservation
{
    public ClaudeUsageObservation(ClaudeUsageSourceKind source, UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Provider != UsageProvider.Claude)
            throw new ArgumentException("Claude snapshot required.", nameof(snapshot));
        Source = source;
        Snapshot = snapshot;
    }

    public ClaudeUsageSourceKind Source { get; }

    public UsageSnapshot Snapshot { get; }

    /// <summary>取得元が値を確定した時刻。</summary>
    public DateTimeOffset ObservedAt => Snapshot.TakenAt;

    /// <summary>監視アプリが受信した時刻。</summary>
    public DateTimeOffset ReceivedAt => Snapshot.ReceivedAt;

    public UsageAvailability Availability => Snapshot.Availability;

    public string? Reason => Snapshot.Reason;

    public string? ClientVersion => Snapshot.ClientVersion;

    /// <summary>
    /// 設定画面へ表示する取得元名。メインウィジェットへは常時表示しない。
    /// </summary>
    public string DisplayName => Source switch
    {
        ClaudeUsageSourceKind.StatusLinePassive => "Claude Code statusLine（常駐セッション）",
        ClaudeUsageSourceKind.StatusLineActive => "Claude Code statusLine（監視アプリ起動セッション）",
        ClaudeUsageSourceKind.CliScreen => "Claude Code CLI /usage 画面",
        _ => "Claude Code",
    };
}
