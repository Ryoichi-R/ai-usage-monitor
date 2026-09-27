using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Core.Presentation;

public static class UsageStatusFormatter
{
    // 常駐アイコンの置き場所の呼び名。WindowsはタスクバーのNotification Area、macOSはメニューバー。
    private static readonly string StatusIconLocation = OperatingSystem.IsMacOS() ? "メニューバー" : "通知領域";

    public static string Format(string providerName, UsageSnapshot snapshot)
    {
        if (snapshot.Provider == UsageProvider.Claude)
            return FormatClaude(snapshot);
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

    // stale化してもTTL超過理由(RECEIVE_TIMEOUT)へ丸めず引き継がれる、利用者操作で解消できる
    // 終端理由・既知の待機理由・取得経路異常。ClaudeUsageObservationMergerのActionableStaleReasons
    // と対応する（RESET_PASSEDとCONSOLE_BUFFER_READ_FAILEDはMerger側で丸めの対象にしていない）。
    // compact widget表示（DisplayMode = Compact）でも同じ理由別文言をそのまま出す。widgetの
    // 視覚デザイン変更（表示切替の新設）を避けるため、短縮形の出し分けは行わない（owner判断）。
    private static string FormatClaudeStaleReason(string? reason) => reason switch
    {
        "CLAUDE_SIGNED_OUT" => "更新が停止 — Claude Codeへサインインしてください",
        "CLAUDE_NOT_INSTALLED" => "更新が停止 — Claude Codeが見つかりません",
        "CLAUDE_TRUST_REQUIRED" => "更新が停止 — 連携設定のフォルダー信頼が必要です",
        "RESET_PASSED" => "5時間枠の更新待ち — 自動再開します",
        "CONSOLE_BUFFER_READ_FAILED" => "取得経路エラー",
        _ => "更新が停止しています",
    };

    // 能動取得をfail-closedで止めた理由のうち、利用者が原因と対処を知る必要があるもの。
    // いずれもstatusLine受信は止めない。macOSの版固定・子孫離脱（D12）と管理設定（D13）。
    private static string? FormatClaudeUnsupportedReason(string? reason) => reason switch
    {
        "CLI_VERSION_REVALIDATION_REQUIRED" => "能動取得を停止 — Claude Codeの版が未検証です",
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

    private static string FormatClaude(UsageSnapshot snapshot) =>
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

    public static string FormatClaudeConnection(UsageSnapshot snapshot) =>
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
