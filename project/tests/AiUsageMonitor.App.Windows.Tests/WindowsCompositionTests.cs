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
        Assert.True(services.OpenSettingsOnTrayDoubleClick);
        Assert.Contains("powershell.exe", services.ClaudeSetupExample!, StringComparison.Ordinal);
        Assert.IsType<ClaudeCliActiveSource>(services.ClaudeSourceFactory(new ClaudeActiveSourceConfiguration(null, "bridge", TimeSpan.FromSeconds(1))));
        var listener = services.CreateClaudeListener!();
        Assert.IsType<ClaudeUsagePipeServer>(listener);
        await listener.DisposeAsync();
        Assert.Equal("AiUsageMonitor.App", typeof(WindowsComposition).Assembly.GetName().Name);

        // Headless screens have no HMONITOR, so no stable display identity is claimed.
        var window = new Avalonia.Controls.Window();
        window.Show();
        try
        {
            Avalonia.Platform.Screen? screen = window.Screens.Primary ?? (window.Screens.All.Count > 0 ? window.Screens.All[0] : null);
            Assert.NotNull(screen);
            Assert.Null(services.ResolveScreenStableId!(screen));
        }
        finally { window.Close(); }
    }
}
