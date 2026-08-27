using System.Globalization;

namespace AiUsageMonitor.Claude.Cli;

/// <summary>
/// 論理スクリーンを既知画面へ分類する。表示文字列のallowlistだけを認識し、
/// 一致しない画面は<see cref="ClaudeCliScreenSignature.Unknown"/>としてfail-closedにする。
/// </summary>
public static class ClaudeCliScreenStateMachine
{
    /// <summary>読み取り対象の最大行数。超過した画面は判定しない。</summary>
    public const int MaximumLines = 400;

    /// <summary>1行あたりの最大文字数。超過した画面は判定しない。</summary>
    public const int MaximumLineLength = 1000;

    private static readonly string[] TrustPromptAnchors =
    [
        "trust this folder",
        "Is this a project you created or one you trust",
        "このフォルダーを信頼",
        "このフォルダを信頼",
    ];

    private static readonly string[] SignedOutAnchors =
    [
        "Sign in to Claude",
        "Please run /login",
        "Invalid API key",
        "ログインしてください",
        // 現行CLI（2.1.220 / 2.1.233 実測）が未ログイン時に表示する文言。
        // "Run /login" 単独はSetupScreenのlogin方式選択画面にも現れるため追加しない。
        // 単独追加すると、その画面をSignedOutへ誤分類する。
        "Not logged in",
    ];

    private static readonly string[] UsageScreenAnchors =
    [
        "Current session",
        "現在のセッション",
        "Current week (all models)",
        "今週（すべてのモデル）",
    ];

    private static readonly string[] SetupScreenAnchors =
    [
        "Choose the text style",
        "Select login method",
        "Let's get started",
        "Press Enter to continue",
        "テキストスタイルを選択",
    ];

    private static readonly string[] ReadyAnchors =
    [
        "? for shortcuts",
        "ショートカットを表示",
        "Try \"",
        "[Screen Reader Mode: on via flag]",
    ];

    /// <summary>
    /// 画面を分類する。判定に使った行以外を保持・記録しない。
    /// </summary>
    public static ClaudeCliScreenSignature Classify(IReadOnlyList<string>? lines)
    {
        if (lines is null || lines.Count == 0) return ClaudeCliScreenSignature.Unknown;
        if (lines.Count > MaximumLines) return ClaudeCliScreenSignature.Unknown;

        var normalized = new List<string>(lines.Count);
        foreach (string? line in lines)
        {
            string value = line ?? string.Empty;
            if (value.Length > MaximumLineLength) return ClaudeCliScreenSignature.Unknown;
            normalized.Add(value);
        }

        // 信頼ダイアログは最優先で判定する。誤って入力を送ると権限の事前承認を押すため。
        if (ContainsAny(normalized, TrustPromptAnchors)) return ClaudeCliScreenSignature.TrustPrompt;
        if (ContainsAny(normalized, SignedOutAnchors)) return ClaudeCliScreenSignature.SignedOut;
        if (ContainsAny(normalized, UsageScreenAnchors)) return ClaudeCliScreenSignature.UsageScreen;
        if (ContainsAny(normalized, SetupScreenAnchors)) return ClaudeCliScreenSignature.SetupScreen;
        // Prompt box alone is not enough: a future setup/trust screen may also contain
        // box drawing characters. Require both a known ready anchor and the input box.
        if (ContainsAny(normalized, ReadyAnchors) && HasPromptBox(normalized)) return ClaudeCliScreenSignature.Ready;

        return ClaudeCliScreenSignature.Unknown;
    }

    /// <summary>
    /// <c>/usage</c>を送出してよい状態かどうか。<see cref="ClaudeCliScreenSignature.Ready"/>だけが対象である。
    /// </summary>
    public static bool AllowsUsageCommand(ClaudeCliScreenSignature signature) =>
        signature == ClaudeCliScreenSignature.Ready;

    /// <summary>
    /// 利用者の承認が必要で、監視アプリが入力を送ってはならない状態かどうか。
    /// </summary>
    public static bool RequiresUserSetup(ClaudeCliScreenSignature signature) =>
        signature is ClaudeCliScreenSignature.TrustPrompt or ClaudeCliScreenSignature.SetupScreen;

    private static bool ContainsAny(List<string> lines, string[] anchors)
    {
        foreach (string line in lines)
        {
            foreach (string anchor in anchors)
            {
                if (CultureInfo.InvariantCulture.CompareInfo.IndexOf(line, anchor, CompareOptions.IgnoreCase) >= 0)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// TUIのprompt入力枠（罫線に続く <c>&gt;</c>）を検出する。ロケール非依存の補助判定である。
    /// </summary>
    private static bool HasPromptBox(List<string> lines)
    {
        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();
            if (string.Equals(trimmed, "$", StringComparison.Ordinal)) return true;
            if (trimmed.Length < 2) continue;
            if (trimmed[0] is '│' or '|' && trimmed[1..].TrimStart().StartsWith('>')) return true;
        }
        return false;
    }
}
