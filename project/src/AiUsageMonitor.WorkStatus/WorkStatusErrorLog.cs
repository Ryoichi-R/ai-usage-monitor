using System.Diagnostics;
using System.Text;

namespace AiUsageMonitor.WorkStatus;

public static class WorkStatusErrorLog
{
    private static readonly object Gate = new();
    public const int MaxBytes = 1024 * 1024;

    public static void Write(string root, Exception error, int limit = MaxBytes)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (limit is < 256 or > MaxBytes) throw new ArgumentOutOfRangeException(nameof(limit));
        string path = WorkStatusPaths.Inside(root, Path.Combine(root, "error.log"));
        byte[] bytes = Encoding.UTF8.GetBytes($"{DateTimeOffset.UtcNow:O} {error.GetType().FullName} HResult=0x{error.HResult:X8}\n" + new StackTrace(error, false) + "\n");
        if (bytes.Length > limit)
        {
            int end = limit;
            while (end > 0 && (bytes[end] & 0xc0) == 0x80) end--;
            bytes = bytes[..end];
        }
        lock (Gate)
        {
            Directory.CreateDirectory(root);
            WorkStatusPaths.RejectLinks(path);
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            if (stream.Length + bytes.Length > limit)
            {
                int keep = (int)Math.Min(stream.Length, limit - bytes.Length);
                byte[] tail = new byte[keep];
                stream.Position = stream.Length - keep;
                stream.ReadExactly(tail);
                int start = Array.IndexOf(tail, (byte)'\n') + 1;
                if (start == 0) start = tail.Length;
                stream.Position = 0;
                stream.Write(tail.AsSpan(start));
                stream.SetLength(stream.Position);
            }
            stream.Position = stream.Length;
            stream.Write(bytes);
        }
    }
}
