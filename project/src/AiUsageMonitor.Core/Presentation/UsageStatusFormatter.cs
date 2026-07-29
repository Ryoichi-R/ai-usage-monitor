using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Core.Presentation;

public static class UsageStatusFormatter
{
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

    private static string FormatClaude(UsageSnapshot snapshot) =>
        snapshot.IsStale || snapshot.Availability == UsageAvailability.Stale
            ? "更新が停止しています"
            : snapshot.Availability switch
            {
                UsageAvailability.Setup => "未接続 — 通知領域から連携設定を開いてください",
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
            ? "更新が停止しています。Claude Codeでプロンプトを実行してください。"
            : snapshot.Availability switch
            {
                UsageAvailability.Available => "Claude Codeと接続しました。",
                UsageAvailability.Setup => "未接続です。下の手順で連携設定を行ってください。",
                UsageAvailability.Loading => "接続を確認しています。",
                UsageAvailability.Waiting => "接続済みです。最初の利用情報を待っています。",
                UsageAvailability.Unavailable => "接続しましたが、利用情報を取得できません。",
                UsageAvailability.NotInstalled => "Claude Codeが見つかりません。公式CLIをインストールしてください。",
                UsageAvailability.SignedOut => "Claude Codeへサインインしてください。",
                UsageAvailability.Unsupported => "このClaude Code versionでは能動取得を利用できません。",
                UsageAvailability.Error => "受信データを確認できません。設定内容を見直してください。",
                _ => "接続状態を確認できません。",
            };
}
