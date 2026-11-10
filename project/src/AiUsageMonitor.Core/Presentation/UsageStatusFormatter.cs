using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Core.Presentation;

public static class UsageStatusFormatter
{
    // 常駐アイコンの置き場所の呼び名。WindowsはタスクバーのNotification Area、macOSはメニューバー。
    private static readonly string StatusIconLocation = OperatingSystem.IsMacOS() ? "メニューバー" : "通知領域";

    public static string Format(string providerName, UsageSnapshot snapshot, bool cliScreen = false)
    {
        if (snapshot.Provider == UsageProvider.Claude)
            return (cliScreen ? FormatClaudeCliReason(snapshot.Reason) : null) ?? FormatClaude(snapshot);
        return snapshot.Reason switch
        {
            "CODEX_HOME_UNAVAILABLE" => "Codexホームを利用できません",
            "CODEX_HOME_DUPLICATE" => "同じCodexホームが別の設定で使われています",
            "DUPLICATE_ACCOUNT" => "同じアカウントが別のCodexホームで使われています",
            "MONITORING_DISABLED" => "監視を停止しています",
            "SIGNED_OUT" => "Codexへサインインしてください",
            "CODEX_NOT_FOUND" => "Codexが見つかりません",
            "METHOD_UNSUPPORTED" => "このCodexでは利用情報を取得できません",
            "SCHEMA_UNSUPPORTED" => "使用上限の形式に対応していません",
            "RPC_TIMEOUT" or "RPC_FAILURE" when snapshot.IsStale => "更新が停止しています",
            "RPC_TIMEOUT" or "RPC_FAILURE" => "利用情報を取得できません",
            _ when snapshot.IsStale || snapshot.Availability == UsageAvailability.Stale => "更新が停止しています",
            _ => snapshot.Availability switch
            {
                UsageAvailability.Loading => "接続を確認しています",
                UsageAvailability.NotInstalled => "Codexが見つかりません",
                UsageAvailability.SignedOut => "ChatGPTへサインインしてください",
                UsageAvailability.Unsupported => "このCodexでは利用情報を取得できません",
                UsageAvailability.Error or UsageAvailability.Unavailable => "利用情報を取得できません",
                _ => string.Empty,
            },
        };
    }

    // stale時の理由別文言。CLI画面の値を表示する経路（cliScreen: true）では、先に
    // FormatClaudeCliReasonがサインアウト・RESET_PASSED・取得経路エラー・読み取り失敗・タイムアウトを
    // CLI用の文言へ変換するため、ここへ落ちるのはそれ以外の理由（未インストール、フォルダー信頼、
    // 能動取得の停止理由など）だけである。statusLine経路（cliScreen: false）ではここが全理由を扱う。
    // その経路のMergerはreset通過をRESET_PASSEDへ、TTL超過をActionableStaleReasons以外は
    // RECEIVE_TIMEOUTへ丸める。statusLine側の文言（「5時間枠の更新待ち」等）はCLI経路の分離後も変えていない。
    // compact widget表示（DisplayMode = Compact）でも同じ理由別文言をそのまま出す。widgetの
    // 視覚デザイン変更（表示切替の新設）を避けるため、短縮形の出し分けは行わない（owner判断）。
    private static string FormatClaudeStaleReason(string? reason) => reason switch
    {
        "CLAUDE_SIGNED_OUT" => "更新が停止 — Claude Codeへサインインしてください",
        "CLAUDE_NOT_INSTALLED" => "更新が停止 — Claude Codeが見つかりません",
        "CLAUDE_TRUST_REQUIRED" => "更新が停止 — 連携設定のフォルダー信頼が必要です",
        "RESET_PASSED" => "5時間枠の更新待ち — 自動再開します",
        "CONSOLE_BUFFER_READ_FAILED" => "取得経路エラー",
        _ => FormatClaudeUnsupportedReason(reason) ?? "更新が停止しています",
    };

    // 能動取得をfail-closedで止めた理由のうち、利用者が原因と対処を知る必要があるもの。
    // いずれもstatusLine受信は止めない。macOSの版固定・子孫離脱（D12）と管理設定（D13）。
    private static string? FormatClaudeUnsupportedReason(string? reason) => reason switch
    {
        "CLI_VERSION_REVALIDATION_REQUIRED" => "能動取得を停止 — Claude Codeの版が未検証です",
        "CLI_SCREEN_INCOMPATIBLE" => "能動取得を停止 — Claude Codeの画面形式が非対応です",
        "CLI_ARGUMENT_INCOMPATIBLE" => "能動取得を停止 — Claude Codeの起動条件が非対応です",
        "CLI_PREVIOUS_ATTEMPT_INCOMPLETE" => "能動取得を停止 — 前回の確認が未完了です",
        "CLI_COMPATIBILITY_STATE_UNAVAILABLE" => "能動取得を停止 — 確認記録を読み書きできません",
        "CLI_LEGACY_MIGRATION_REQUIRED" => "能動取得を停止 — 旧停止記録の確認が必要です",
        "CLI_IDENTITY_MISMATCH" => "能動取得を停止 — 実行ファイルが一致しません",
        "PROCESS_CLEANUP_FAILED" => "能動取得を停止 — 終了確認ができません",
        "CLI_GROUP_ESCAPE_DETECTED" => "能動取得を停止 — 子プロセスの離脱を検出しました",
        "MANAGED_SETTINGS_PRESENT" => "能動取得を停止 — 管理設定があります",
        _ => null,
    };

    private static string? FormatClaudeUnsupportedConnection(string? reason) => reason switch
    {
        "CLI_VERSION_REVALIDATION_REQUIRED" =>
            "Claude Codeの版が検証済みの版と異なるため、能動取得を停止しています。新しい版の再検証が済むまで再開しません。statusLine受信は引き続き使えます。",
        "CLI_GROUP_ESCAPE_DETECTED" =>
            "Claude Codeの子プロセスが監視範囲から外れたため、能動取得を停止しました。再検証が済むまで再開しません。statusLine受信は引き続き使えます。troubleshootingを確認してください。",
        "MANAGED_SETTINGS_PRESENT" =>
            "管理設定（managed settings）が存在するため、能動取得を行いません。statusLine受信は引き続き使えます。",
        _ => null,
    };

    /// <summary>
    /// statusLine参考値の表示に添える、公式CLI（能動取得）の失敗理由の利用者向け文言。
    /// 理由コードは画面に出さず、診断ログと接続設定の詳細にだけ残す。
    /// </summary>
    public static string FormatClaudeCliFailure(string? reason) =>
        FormatClaudeCliReason(reason) ??
        FormatClaudeUnsupportedReason(reason) ??
        reason switch
        {
            "CLAUDE_NOT_INSTALLED" => "Claude Codeが見つかりません",
            "CLAUDE_TRUST_REQUIRED" => "連携設定のフォルダー信頼が必要です",
            _ => "利用情報を取得できません",
        };

    private static string? FormatClaudeCliReason(string? reason) => reason switch
    {
        "CLAUDE_SIGNED_OUT" => "Claude Codeへサインインしてください",
        "RESET_PASSED" => "利用枠の更新待ち — 自動再開します",
        "CONSOLE_BUFFER_READ_FAILED" => "取得経路エラー",
        _ when ClaudeUsageFailure.IsReadingFailure(reason) => "Claude の利用枠を読み取れません",
        _ when ClaudeUsageFailure.IsTimeout(reason) => "利用情報を取得できません",
        _ => null,
    };

    private static string? FormatClaudeCliConnection(string? reason) => reason switch
    {
        "CLAUDE_SIGNED_OUT" => "Claude Codeへサインインしてください。",
        "RESET_PASSED" => "利用枠の更新待ちです。自動的に再開します。",
        "CONSOLE_BUFFER_READ_FAILED" => "Claude利用情報の取得経路でエラーが発生しました。アプリ再起動後も続く場合はtroubleshootingを確認してください。",
        _ when ClaudeUsageFailure.IsReadingFailure(reason) => "Claude Code の利用枠を更新できません。自動で再試行します。",
        _ when ClaudeUsageFailure.IsTimeout(reason) => "接続しましたが、利用情報を取得できません。",
        _ => null,
    };

    private static string FormatClaude(UsageSnapshot snapshot) =>
        snapshot.Availability == UsageAvailability.SignedOut ? "Claude Codeへサインインしてください" :
        snapshot.Reason == "CLI_COMPATIBILITY_CHECKING" ? "Claude Codeの変更後の互換性を確認しています" :
        snapshot.Reason == "CLI_CONFIRMATION_RETRYING" ? "互換性を確認できない状態が続いています — 自動再試行します" :
        snapshot.IsStale || snapshot.Availability == UsageAvailability.Stale
            ? FormatClaudeStaleReason(snapshot.Reason)
            : snapshot.Availability switch
            {
                UsageAvailability.Unsupported when FormatClaudeUnsupportedReason(snapshot.Reason) is { } message => message,
                UsageAvailability.Setup => $"未接続 — {StatusIconLocation}から連携設定を開いてください",
                UsageAvailability.Loading => "接続を確認しています",
                UsageAvailability.Waiting => "接続済み — 利用情報を待っています",
                UsageAvailability.Unavailable => "利用情報を取得できません",
                UsageAvailability.NotInstalled => "Claude Codeが見つかりません",
                UsageAvailability.SignedOut => "Claude Codeへサインインしてください",
                UsageAvailability.Unsupported => "このClaude Codeでは能動取得を利用できません",
                UsageAvailability.Error => "連携エラー",
                _ => "状態を確認できません",
            };

    public static string FormatClaudeConnection(UsageSnapshot snapshot, bool cliScreen = false) =>
        (cliScreen ? FormatClaudeCliConnection(snapshot.Reason) : null) ??
        FormatClaudeConnectionCore(snapshot);

    private static string FormatClaudeConnectionCore(UsageSnapshot snapshot) =>
        snapshot.IsStale || snapshot.Availability == UsageAvailability.Stale
            ? snapshot.Reason switch
            {
                "CLAUDE_SIGNED_OUT" => "更新が停止しています。Claude Codeへサインインしてください。",
                "CLAUDE_NOT_INSTALLED" => "更新が停止しています。Claude Codeが見つかりません。公式CLIをインストールしてください。",
                "CLAUDE_TRUST_REQUIRED" => "更新が停止しています。連携設定のフォルダー信頼が必要です。Claude Code側でフォルダーを信頼してください。",
                "RESET_PASSED" => "5時間枠の更新待ちです。自動的に再開します。しばらく待っても再開しない場合はお知らせください。",
                "CONSOLE_BUFFER_READ_FAILED" => "Claude利用情報の取得経路でエラーが発生しました。アプリ再起動後も続く場合はtroubleshootingを確認してください。",
                _ => "更新が停止しています。Claude Codeでプロンプトを実行してください。",
            }
            : snapshot.Availability switch
            {
                UsageAvailability.Available => "Claude Codeと接続しました。",
                UsageAvailability.Setup => "未接続です。下の手順で連携設定を行ってください。",
                UsageAvailability.Loading => "接続を確認しています。",
                UsageAvailability.Waiting => "接続済みです。最初の利用情報を待っています。",
                UsageAvailability.Unavailable => "接続しましたが、利用情報を取得できません。",
                UsageAvailability.NotInstalled => "Claude Codeが見つかりません。公式CLIをインストールしてください。",
                UsageAvailability.SignedOut => "Claude Codeへサインインしてください。",
                UsageAvailability.Unsupported when FormatClaudeUnsupportedConnection(snapshot.Reason) is { } message => message,
                UsageAvailability.Unsupported => "このClaude Code versionでは能動取得を利用できません。",
                UsageAvailability.Error => "受信データを確認できません。設定内容を見直してください。",
                _ => "接続状態を確認できません。",
            };
}
