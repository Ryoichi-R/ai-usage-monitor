using AiUsageMonitor.Platform;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace AiUsageMonitor.Platform.Windows.Process;

/// <summary>Job Objectで子プロセスをアタッチするIProcessLifetimeGuardFactoryのWindows実装。</summary>
public sealed class WindowsProcessLifetimeGuardFactory : IProcessLifetimeGuardFactory
{
    public IProcessLifetimeGuard Attach(DiagnosticsProcess process) => ProcessJobObject.Attach(process);
}
