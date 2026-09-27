using System.Runtime.Versioning;

namespace AiUsageMonitor.Claude.Mac;

/// <summary>
/// 子孫のgroup離脱を検出したCLI versionのactive取得を、再検証まで止める記録（D12）。
/// 内容は検証済みversion文字列だけで、画面・利用値・認証情報を含めない。
/// 再検証後にownerがfileを削除するか、検証済みversionを更新すると解除される。
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class ClaudeActiveQuarantine(string path)
{
    public string Path { get; } = path;

    /// <summary>現在の検証済みversionが隔離中ならtrue。読めない場合もfail-closedでtrueを返す。</summary>
    public bool IsActive()
    {
        try { return string.Equals(File.ReadAllText(Path).Trim(), ClaudeLaunchPolicy.VerifiedVersion, StringComparison.Ordinal); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    public void Record()
    {
        string directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string temporary = Path + ".tmp";
        File.WriteAllText(temporary, ClaudeLaunchPolicy.VerifiedVersion + "\n");
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, Path, true);
    }
}
