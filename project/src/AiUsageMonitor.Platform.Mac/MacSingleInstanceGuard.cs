using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

using System.Runtime.Versioning;

namespace AiUsageMonitor.Platform.Mac;

/// <summary>
/// 専用ロックファイルへのflock(LOCK_EX | LOCK_NB)で多重起動を防止するISingleInstanceGuardのmacOS実装。
/// ロックはfile descriptorに結び付くため、プロセスが異常終了してもOSが解放し、staleなロックは残らない。
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacSingleInstanceGuard : ISingleInstanceGuard
{
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int WouldBlock = 35; // EWOULDBLOCK（macOS）

    private readonly string _lockPath;
    private SafeFileHandle? _handle;
    private bool _acquired;
    private bool _disposed;

    public MacSingleInstanceGuard(string lockPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        _lockPath = lockPath;
    }

    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_acquired) return true;
        string? directory = Path.GetDirectoryName(_lockPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // .NET File.OpenHandle takes its own shared flock even with FileShare.ReadWrite.
        // Use native open so the competing exclusive flock yields EWOULDBLOCK, not an IOException.
        if (!File.Exists(_lockPath))
        {
            try
            {
                using var created = new FileStream(_lockPath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.ReadWrite,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
            }
            catch (IOException) when (File.Exists(_lockPath)) { }
        }
        // open with two arguments avoids the ARM64 variadic ABI required by O_CREAT's mode argument.
        int fd = open(_lockPath, 0x2 | 0x1000000 | 0x100); // RDWR|CLOEXEC|NOFOLLOW
        if (fd < 0) throw new IOException($"Failed to open the instance lock (errno {Marshal.GetLastPInvokeError()}).");
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        if (flock(handle, LockExclusive | LockNonBlocking) == 0)
        {
            _handle = handle;
            _acquired = true;
            return true;
        }
        int error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        if (error == WouldBlock) return false;
        throw new IOException($"Failed to lock the instance file (errno {error}).");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // closeでflockも解放される。ファイル自体は次回起動で再利用するため削除しない（削除と再作成の競合を避ける）。
        _handle?.Dispose();
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int flock(SafeFileHandle fd, int operation);
}
