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
}
