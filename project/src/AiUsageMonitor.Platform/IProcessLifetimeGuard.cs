namespace AiUsageMonitor.Platform;

/// <summary>
/// 子プロセスを監視アプリ終了時に残さないための生存管理ハンドル。
/// Dispose時（または監視アプリの異常終了をOSが検知した時）に配下のプロセスを終了させる。
/// Windows実装はJob Object、macOS実装はsetpgid + kill(-pgid)で実現する。
/// </summary>
public interface IProcessLifetimeGuard : IDisposable
{
}

/// <summary>起動済みプロセスをIProcessLifetimeGuardへ結び付けるファクトリ。</summary>
public interface IProcessLifetimeGuardFactory
{
    IProcessLifetimeGuard Attach(System.Diagnostics.Process process);
}
