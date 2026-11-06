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

    internal static string? IsolatedRoot { get; private set; }
    internal static string CurrentActivationName => IsolatedRoot is null ? ActivationName : ActivationName + "-WorkStatus-" + AiUsageMonitor.WorkStatus.HookNormalizer.Digest(IsolatedRoot);

    [STAThread]
    public static int Main(string[] args)
    {
        if (ClaudeConsoleHelper.TryHandle(args, Console.Out)) return 0;
        if (args is ["--work-status-test-root", var root])
        {
            if (!Path.IsPathFullyQualified(root)) return 1;
            IsolatedRoot = Path.GetFullPath(root);
        }
        string instanceName = IsolatedRoot is null ? "Local\\CodexUsageMonitor-5C898151" : CurrentActivationName + "-instance";
        using var instance = new WindowsSingleInstanceGuard(instanceName);
        if (!instance.TryAcquire())
        {
            WindowsInstanceActivationChannel.TrySignal(CurrentActivationName);
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
            var lifecycle = WindowsProductLifecycle.Start(() => desktop.Shutdown(), WindowsComposition.Create(Program.IsolatedRoot), Program.CurrentActivationName);
            desktop.ShutdownRequested += (_, e) => lifecycle.OnShutdownRequested(e);
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal static class WindowsComposition
{
    internal static WidgetHostServices Create(string? testRoot = null)
    {
        var paths = testRoot is null ? new WindowsAppPathProvider() : new WindowsAppPathProvider(testRoot);
        string directory = AppContext.BaseDirectory;
        var guards = new WindowsProcessLifetimeGuardFactory();
        var diagnostics = new WindowsDiagnosticLog(Path.Combine(Path.GetDirectoryName(paths.SettingsFilePath)!, "diagnostics.log"));
        return new WidgetHostServices
        {
            AppPaths = paths,
            WorkStatusOnly = testRoot is not null,
            UserProfile = testRoot ?? paths.UserHomeDirectory,
            ReadClaudeBusySince = testRoot is null
                ? new ClaudeDesktopActivity(paths.UserHomeDirectory, Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")).ReadBusySince : null,
            InheritedCodexHome = testRoot is null ? Environment.GetEnvironmentVariable("CODEX_HOME") : null,
            Diagnostic = diagnostics.Write,
            Startup = testRoot is null ? new WindowsStartupService() : new AiUsageMonitor.Platform.NoStartupService(),
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
