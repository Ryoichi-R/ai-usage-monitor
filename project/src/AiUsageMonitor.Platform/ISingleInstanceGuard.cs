namespace AiUsageMonitor.Platform;

/// <summary>
/// 監視アプリの多重起動を防止する。Windows実装は名前付きMutex、
/// macOS実装は専用ロックファイルへのflock（passive取得用Unixドメインソケットとは別path・別責務）を使う。
/// </summary>
public interface ISingleInstanceGuard : IDisposable
{
    /// <summary>最初のインスタンスであればtrueを返し、以後の呼び出し元は起動を継続してよい。</summary>
    bool TryAcquire();
}
