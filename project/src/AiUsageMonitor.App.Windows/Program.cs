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
using Avalonia.Threading;

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
            var services = WindowsComposition.Create();
            WidgetHost? host = null;
            WindowsInstanceActivationChannel? activation = null;
            bool stopping = false;
            async Task StopAsync()
            {
                if (stopping) return;
                stopping = true;
                try { if (host is not null) await host.DisposeAsync(); }
                finally { activation?.Dispose(); desktop.Shutdown(); }
            }
            host = new WidgetHost(services, () => _ = StopAsync());
            activation = WindowsInstanceActivationChannel.Listen(Program.ActivationName,
                () => Dispatcher.UIThread.Post(host.ShowWidget));
            desktop.ShutdownRequested += (_, e) =>
            {
                if (stopping) return;
                e.Cancel = true;
                _ = StopAsync();
            };
            Dispatcher.UIThread.Post(async () =>
            {
                try { await host.StartAsync(); }
                catch (Exception)
                {
                    var notice = new AiUsageMonitor.App.UI.Views.NoticeWindow("AI Usage Monitorを起動できませんでした。設定とインストール先を確認してください。");
                    notice.Closed += (_, _) => _ = StopAsync();
                    notice.Show();
                }
            });
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
        return new WidgetHostServices
        {
            AppPaths = paths,
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
        };
    }
}
