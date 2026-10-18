namespace AiUsageMonitor.Platform;

/// <summary>
/// OSのファイルマネージャーでフォルダーを開く。Windows実装はexplorer.exe、macOS実装はopenを使う。
/// </summary>
public interface IShellOpener
{
    void OpenFolder(string path);
}
