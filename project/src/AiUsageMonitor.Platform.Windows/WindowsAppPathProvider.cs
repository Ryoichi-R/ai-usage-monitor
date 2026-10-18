using AiUsageMonitor.Platform;

namespace AiUsageMonitor.Platform.Windows;

/// <summary>
/// LocalApplicationData配下を用途別に解決するIAppPathProviderのWindows実装。
/// 製品改名前の互換のためディレクトリ名はCodexUsageMonitorを維持する。
/// </summary>
public sealed class WindowsAppPathProvider : IAppPathProvider
{
    private const string AppDataDirectoryName = "CodexUsageMonitor";

    public WindowsAppPathProvider()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    /// <summary>
    /// LocalApplicationDataのルートを明示指定する。テストや、既定のユーザープロファイル外へ
    /// 状態を隔離したい運用のために公開している。
    /// </summary>
    public WindowsAppPathProvider(string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        SettingsFilePath = Path.Combine(localApplicationData, AppDataDirectoryName, "settings.json");
        ClaudeWorkspaceDirectory = Path.Combine(localApplicationData, AppDataDirectoryName, "ClaudeCliWorkspace");
        TemporaryDirectory = Path.Combine(localApplicationData, AppDataDirectoryName, "Temp");
        UserHomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public string SettingsFilePath { get; }
    public string ClaudeWorkspaceDirectory { get; }
    public string TemporaryDirectory { get; }
    public string UserHomeDirectory { get; }
}
