namespace AiUsageMonitor.Claude.Acquisition;

/// <summary>
/// Claude利用率の取得元。値の由来を診断・source arbitrationで区別するために使用する。
/// </summary>
public enum ClaudeUsageSourceKind
{
    /// <summary>利用者が常駐させているClaude Code sessionのstatusLineから受動的に届いた観測。</summary>
    StatusLinePassive,

    /// <summary>監視アプリが起動したsessionのstatusLineから、専用pipe経由で届いた観測（経路B1）。</summary>
    StatusLineActive,

    /// <summary>監視アプリが起動したsessionの<c>/usage</c>画面をparseした観測（経路B2）。</summary>
    CliScreen,
}
