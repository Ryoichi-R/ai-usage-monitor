using System.Globalization;

namespace AiUsageMonitor.App.Mac;

/// <summary>
/// 理由コードと例外の型名だけを~/Library/Logs/AiUsageMonitor/diagnostic.logへ追記する。
/// 例外メッセージ・usage・account・生画面は書かない。1 MiBを超えたら1世代だけ残して切り替える。
/// </summary>
internal sealed class DiagnosticLog
{
    private const long MaximumBytes = 1024 * 1024;
    private readonly object _sync = new();
    private readonly string _path;

    public DiagnosticLog(string userHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userHome);
        _path = Path.Combine(userHome, "Library", "Logs", "AiUsageMonitor", "diagnostic.log");
    }

    public string FilePath => _path;

    public void Write(string code, Exception? exception)
    {
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} {code}{(exception is null ? string.Empty : " " + exception.GetType().Name)}\n");
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var file = new FileInfo(_path);
                if (file.Exists && file.Length > MaximumBytes) File.Move(_path, _path + ".1", overwrite: true);
                File.AppendAllText(_path, line);
            }
            catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException)
            {
                // 診断ログの失敗でアプリを止めない。
            }
        }
    }
}
