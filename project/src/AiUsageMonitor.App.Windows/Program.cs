using AiUsageMonitor.App.UI.Hosting;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Claude.Configuration;
using AiUsageMonitor.Claude.Transport;
using AiUsageMonitor.Claude.Windows.Cli;
using AiUsageMonitor.Claude.Windows.Console;
using AiUsageMonitor.Platform.Windows;
using AiUsageMonitor.Platform.Windows.Process;
using AiUsageMonitor.Platform.Windows.Startup;
using AiUsageMonitor.Platform.Windows.Window;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace AiUsageMonitor.App.Windows;

internal static class Program
{
    internal const string ActivationName = "Local\\AiUsageMonitor-Activate-5C898151";

    [STAThread]
    public static int Main(string[] args)
    {
        if (ClaudeConsoleHelper.TryHandle(args, Console.Out)) return 0;
        using var instance = new WindowsSingleInstanceGuard("Local\\CodexUsageMonitor-5C898151");
        if (!instance.TryAcquire())
        {
            WindowsInstanceActivationChannel.TrySignal(ActivationName);
            return 0;
        }
        return AppBuilder.Configure<WindowsApp>().UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }
}

internal sealed class WindowsApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var lifecycle = WindowsProductLifecycle.Start(() => desktop.Shutdown(), WindowsComposition.Create(), Program.ActivationName);
            desktop.ShutdownRequested += (_, e) => lifecycle.OnShutdownRequested(e);
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal static class WindowsComposition
{
    internal static WidgetHostServices Create()
    {
        var paths = new WindowsAppPathProvider();
        string directory = AppContext.BaseDirectory;
        var guards = new WindowsProcessLifetimeGuardFactory();
        var diagnostics = new WindowsDiagnosticLog(Path.Combine(Path.GetDirectoryName(paths.SettingsFilePath)!, "diagnostics.log"));
        return new WidgetHostServices
        {
            AppPaths = paths,
            Diagnostic = diagnostics.Write,
            Startup = new WindowsStartupService(),
            ShellOpener = new WindowsShellOpener(),
            CreateLayerController = () => new WindowsWidgetLayerController(),
            ClaudeWorkspace = new AiUsageMonitor.Claude.Windows.Process.ClaudeWorkspaceProvisioner(paths),
            ClaudeBridgePath = Path.Combine(directory, "claude-statusline-bridge.ps1"),
            ClaudeSetupExample = ClaudeSetupExample.Create(directory),
            CreateClaudeListener = () => new ClaudeUsagePipeServer(),
            ClaudeSourceFactory = configuration => new ClaudeCliActiveSource(
                configuration.ExecutablePath, configuration.BridgePath, configuration.StartupTimeout,
                new AiUsageMonitor.Claude.Windows.Process.ClaudeWorkspaceProvisioner(paths),
                new WindowsClaudeScreenSessionFactory(),
                new AiUsageMonitor.Claude.Windows.Process.ClaudeExecutableLocator()),
            CodexLifetimeGuardFactory = process => guards.Attach(process),
            ResolveScreenStableId = screen =>
            {
                var handle = screen.TryGetPlatformHandle();
                var native = NativeMonitorApi.Instance;
                return handle?.HandleDescriptor == "HMONITOR" && native.TryGetMonitorInfo(handle.Handle, out var monitor, out _)
                    ? native.GetStableId(monitor.DeviceName) : null;
            },
            ReadmePath = Path.Combine(directory, "README.md"),
            StatusIcon = new WindowIcon(Path.Combine(directory, "ai-usage-monitor.ico")),
            OpenSettingsOnTrayDoubleClick = true,
        };
    }
}
