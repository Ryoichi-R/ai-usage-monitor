namespace AiUsageMonitor.Platform.Mac;

/// <summary>
/// IAppPathProviderのmacOS実装。用途別に~/Library/Application Support/AiUsageMonitor配下を明示的に返す。
/// Environment.SpecialFolder.LocalApplicationDataの暗黙マッピング（macOSでは~/.local/share）には依存しない。
/// </summary>
public sealed class MacAppPathProvider : IAppPathProvider
{
    public const string AppDirectoryName = "AiUsageMonitor";

    public MacAppPathProvider()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    /// <summary>ホームディレクトリを明示指定する。テストや、状態を別の場所へ隔離する運用のために公開している。</summary>
    public MacAppPathProvider(string userHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userHome);
        UserHomeDirectory = userHome;
        ApplicationSupportDirectory = Path.Combine(userHome, "Library", "Application Support", AppDirectoryName);
        SettingsFilePath = Path.Combine(ApplicationSupportDirectory, "settings.json");
        ClaudeWorkspaceDirectory = Path.Combine(ApplicationSupportDirectory, "ClaudeCliWorkspace");
        TemporaryDirectory = Path.Combine(ApplicationSupportDirectory, "Temp");
        InstanceLockPath = Path.Combine(ApplicationSupportDirectory, "instance.lock");
        LaunchAgentsDirectory = Path.Combine(userHome, "Library", "LaunchAgents");
    }

    public string SettingsFilePath { get; }
    public string ClaudeWorkspaceDirectory { get; }
    public string TemporaryDirectory { get; }
    public string UserHomeDirectory { get; }

    /// <summary>アプリの状態を置く親ディレクトリ。</summary>
    public string ApplicationSupportDirectory { get; }

    /// <summary>多重起動防止のflock専用ファイル。passive取得のsocketとは別path・別責務とする。</summary>
    public string InstanceLockPath { get; }

    /// <summary>ログイン時自動起動のLaunchAgent plistを置くディレクトリ。</summary>
    public string LaunchAgentsDirectory { get; }
}
