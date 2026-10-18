namespace AiUsageMonitor.Core.Settings;

/// <summary>利用者が選択できるClaude利用率の取得方法。</summary>
public enum ClaudeUsageAcquisitionMode
{
    /// <summary>公式CLIを周期取得の優先観測とし、statusLineは明示的な参考fallbackに使う。</summary>
    Automatic = 0,

    /// <summary>利用者のClaude Code sessionから届くstatusLineだけを使用する。</summary>
    StatusLineOnly = 1,

    /// <summary>監視アプリが起動する公式CLIだけを使用する。</summary>
    OfficialCliOnly = 2,
}
