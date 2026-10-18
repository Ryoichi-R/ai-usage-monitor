using DiagnosticsProcess = System.Diagnostics.Process;

namespace AiUsageMonitor.Claude.Windows.Process;

[Obsolete(
    "Use AiUsageMonitor.Platform.Windows.Process.ProcessJobObject. This forwarding type is retained for binary compatibility.")]
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class ProcessJobObject : IDisposable
{
    private readonly AiUsageMonitor.Platform.Windows.Process.ProcessJobObject _inner;

    private ProcessJobObject(
        AiUsageMonitor.Platform.Windows.Process.ProcessJobObject inner) =>
        _inner = inner;

    public static ProcessJobObject Attach(DiagnosticsProcess process) =>
        new(AiUsageMonitor.Platform.Windows.Process.ProcessJobObject.Attach(process));

    public void Dispose() => _inner.Dispose();
}
