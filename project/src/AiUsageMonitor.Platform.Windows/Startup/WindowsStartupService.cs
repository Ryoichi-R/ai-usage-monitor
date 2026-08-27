using System.Diagnostics.CodeAnalysis;
using AiUsageMonitor.Platform;

namespace AiUsageMonitor.Platform.Windows.Startup;

/// <summary>レジストリRunキーでログイン時自動起動を制御するIStartupServiceのWindows実装。</summary>
[ExcludeFromCodeCoverage(
    Justification = "Thin forwarder to StartupRegistryService.Apply, which mutates the real registry and is itself excluded.")]
public sealed class WindowsStartupService : IStartupService
{
    public void Apply(bool startWithSystem) => StartupRegistryService.Apply(startWithSystem);
}
