using AiUsageMonitor.App.UI.Runtime;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Claude.Transport;
using AiUsageMonitor.Platform.Windows;
using Avalonia.Headless.XUnit;

namespace AiUsageMonitor.App.Windows.Tests;

public sealed class WindowsCompositionTests
{
    [AvaloniaFact]
    public async Task CompositionPreservesWindowsPathsProvidersAndJobObjectBoundary()
    {
        var services = WindowsComposition.Create();
        Assert.IsType<WindowsAppPathProvider>(services.AppPaths);
        Assert.IsType<WindowsDiagnosticLog>(services.Diagnostic.Target);
        Assert.EndsWith(Path.Combine("CodexUsageMonitor", "settings.json"), services.AppPaths.SettingsFilePath, StringComparison.Ordinal);
        Assert.NotNull(services.CodexLifetimeGuardFactory);
        Assert.Null(services.CodexProcessLauncher);
        Assert.NotNull(services.StatusIcon);
        Assert.Contains("powershell.exe", services.ClaudeSetupExample!, StringComparison.Ordinal);
        Assert.IsType<ClaudeCliActiveSource>(services.ClaudeSourceFactory(new ClaudeActiveSourceConfiguration(null, "bridge", TimeSpan.FromSeconds(1))));
        var listener = services.CreateClaudeListener!();
        Assert.IsType<ClaudeUsagePipeServer>(listener);
        await listener.DisposeAsync();
        Assert.Equal("AiUsageMonitor.App", typeof(WindowsComposition).Assembly.GetName().Name);
    }
}
