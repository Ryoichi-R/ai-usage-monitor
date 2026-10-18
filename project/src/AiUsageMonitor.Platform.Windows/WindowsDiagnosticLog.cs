using System.Globalization;
using System.Text;

namespace AiUsageMonitor.Platform.Windows;

/// <summary>理由コードと例外の型・番号だけを記録する、サイズ制限付きのローカル診断。</summary>
public sealed class WindowsDiagnosticLog
{
    private readonly string _path;
    private readonly long _maximumBytes;
    private readonly object _gate = new();

    public WindowsDiagnosticLog(string path, long maximumBytes = 256 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 256);
        _path = Path.GetFullPath(path);
        _maximumBytes = maximumBytes;
    }

    public void Write(string code, Exception? exception)
    {
        // メッセージ・stack trace・provider payload・任意の改行は記録しない。
        string safeCode = !string.IsNullOrEmpty(code) && code.Length <= 160 &&
            code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':')
            ? code : "invalid-diagnostic-code";
        string error = exception is null ? "-" : string.Create(CultureInfo.InvariantCulture,
            $"{exception.GetType().Name}:0x{exception.HResult:X8}");
        string line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:O} {safeCode} {error}\n");
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (File.Exists(_path) && new FileInfo(_path).Length + Encoding.UTF8.GetByteCount(line) > _maximumBytes)
                    File.Move(_path, _path + ".1", overwrite: true);
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // 診断先の書き込み失敗でウィジェットを停止させない。
            }
        }
    }
}
