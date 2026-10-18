namespace AiUsageMonitor.Platform;

/// <summary>
/// アプリが解決する全OS標準フォルダーを用途別に返す。
/// Environment.GetFolderPath(SpecialFolder.LocalApplicationData)等の暗黙マッピングに
/// 個別箇所が依存しないよう、設定・Claude workspace・実行ファイル探索の基点をここへ集約する。
/// Windows実装はLocalApplicationData配下、macOS実装は明示的に~/Library/Application Support/AiUsageMonitor等を返す。
/// </summary>
public interface IAppPathProvider
{
    /// <summary>設定ファイル（settings.json）の完全パス。</summary>
    string SettingsFilePath { get; }

    /// <summary>Claude CLIを起動する専用workspaceのディレクトリ。</summary>
    string ClaudeWorkspaceDirectory { get; }

    /// <summary>一時ファイル（statusLine bridge用の一時settings等）を置くディレクトリ。</summary>
    string TemporaryDirectory { get; }

    /// <summary>ユーザーのホームディレクトリ（実行ファイル探索の既定候補算出に使う）。</summary>
    string UserHomeDirectory { get; }
}
