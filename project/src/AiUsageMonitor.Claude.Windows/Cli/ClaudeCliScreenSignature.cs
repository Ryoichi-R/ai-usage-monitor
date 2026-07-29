namespace AiUsageMonitor.Claude.Windows.Cli;

/// <summary>
/// 隠しコンソールから読み取った論理スクリーンの分類。
/// 数値抽出には使用せず、「どの画面で止まっているか」の判定にだけ使用する。
/// </summary>
public enum ClaudeCliScreenSignature
{
    /// <summary>既知のいずれにも一致しない。fail-closedで終了する。</summary>
    Unknown = 0,

    /// <summary>ワークスペース信頼ダイアログ。入力を送らず終了する。</summary>
    TrustPrompt,

    /// <summary>theme選択等のonboarding。入力を送らず終了する。</summary>
    SetupScreen,

    /// <summary>未認証。</summary>
    SignedOut,

    /// <summary>通常のprompt入力待ち。<c>/usage</c>を送出できる唯一の状態。</summary>
    Ready,

    /// <summary><c>/usage</c>表示中。</summary>
    UsageScreen,
}
