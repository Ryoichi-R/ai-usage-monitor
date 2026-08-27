using AiUsageMonitor.Platform;
using AiUsageMonitor.Platform.Windows.Process;
using DiagnosticsProcess = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace AiUsageMonitor.Platform.Windows.Tests;

public sealed class WindowsProcessLifetimeGuardFactoryTests
{
    [Fact]
    public void AttachReturnsADisposableGuardForARunningProcess()
    {
        using DiagnosticsProcess process = DiagnosticsProcess.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var factory = new WindowsProcessLifetimeGuardFactory();

        using IProcessLifetimeGuard guard = factory.Attach(process);

        Assert.NotNull(guard);
        process.WaitForExit(5000);
    }
}
