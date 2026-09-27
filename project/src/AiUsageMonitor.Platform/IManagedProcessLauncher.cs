using System.Diagnostics;

namespace AiUsageMonitor.Platform;

/// <summary>生成前の監督準備とstdio・終了管理を所有する境界。</summary>
public interface IManagedProcessLauncher
{
    Task<IManagedProcessSession> StartAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken);
}

public interface IManagedProcessSession : IAsyncDisposable
{
    StreamReader StandardOutput { get; }
    StreamReader StandardError { get; }
    StreamWriter StandardInput { get; }

    /// <summary>監督側が報告した後始末の結果。<see cref="IAsyncDisposable.DisposeAsync"/>完了後に確定する。</summary>
    ManagedProcessOutcome Outcome => ManagedProcessOutcome.Unknown;
}

/// <summary>監督下のprocess groupを終了したときの後始末の結果。</summary>
public enum ManagedProcessOutcome
{
    /// <summary>未確定、または結果を報告しない実装（WindowsのJob Object等）。</summary>
    Unknown,

    /// <summary>追跡した全processを終了・回収した。</summary>
    Clean,

    /// <summary>子孫がprocess group／sessionを離脱した。個別に終了済みだが、取得はfail-closedにする（D12）。</summary>
    DescendantEscaped,

    /// <summary>回収上限内に消滅を確認できなかった、追跡容量を超えた、または結果の報告がなかった。</summary>
    SupervisionFailed,
}
