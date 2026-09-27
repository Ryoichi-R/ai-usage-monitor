using System.Runtime.Versioning;
using AiUsageMonitor.App.UI.Hosting;
using AiUsageMonitor.Platform.Mac;
using AiUsageMonitor.Claude.Mac;
using AiUsageMonitor.Claude.Cli;

namespace AiUsageMonitor.App.Mac;

/// <summary>macOS用composition root。App.UIの<see cref="WidgetHost"/>へPlatform.Macの実装を注入する（D9）。</summary>
[SupportedOSPlatform("macos")]
internal static class MacComposition
{
    public static WidgetHostServices Create(MacAppPathProvider paths, DiagnosticLog log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        string baseDirectory = AppContext.BaseDirectory;
        var workspace = new AppPathClaudeWorkspace(paths);
        string helper = Path.Combine(baseDirectory, "ai-usage-process-supervisor");
        string journal = Path.Combine(paths.ApplicationSupportDirectory, "ProcessSessions");
        string bridge = Path.Combine(baseDirectory, "ai-usage-claude-statusline");
        var policy = new ClaudeLaunchPolicy(paths.UserHomeDirectory);
        var quarantine = new ClaudeActiveQuarantine(Path.Combine(paths.ApplicationSupportDirectory, "claude-active-quarantine"));
        var probe = new MacClaudeCapabilityProbe(new MacManagedProcessLauncher(helper, journal, verifyClaude: true), policy, workspace, quarantine);
        return new WidgetHostServices
        {
            AppPaths = paths,
            Startup = new LaunchAgentStartupService(
                paths.LaunchAgentsDirectory,
                () => LaunchAgentStartupService.ResolveProgramArguments(
                    Environment.ProcessPath ?? throw new InvalidOperationException("The process path is unavailable."),
                    typeof(MacComposition).Assembly.Location)),
            ShellOpener = new MacShellOpener(),
            CreateLayerController = () => new MacWidgetLayerController(),
            ClaudeSourceFactory = configuration => new ClaudeCliActiveSource(
                configuration.ExecutablePath, bridge, configuration.StartupTimeout, workspace,
                new MacClaudeScreenSessionFactory(new MacManagedProcessLauncher(helper, journal, usePty: true, verifyClaude: true), policy, quarantine, code => log.Write(code, null)),
                new MacClaudeExecutableLocator(paths.UserHomeDirectory, helper, policy), probe.ProbeAsync),
            ClaudeWorkspace = workspace,
            ClaudeBridgePath = bridge,
            ClaudeSetupExample = ClaudeStatusLineBridge.CreateSetupExample(bridge),
            CreateClaudeListener = () => new ClaudeUsageSocketListener(),
            CodexProcessLauncher = new MacManagedProcessLauncher(
                Path.Combine(baseDirectory, "ai-usage-process-supervisor"),
                Path.Combine(paths.ApplicationSupportDirectory, "ProcessSessions")),
            ResolveScreenStableId = screen =>
            {
                Avalonia.Platform.IPlatformHandle? handle = screen.TryGetPlatformHandle();
                return handle is null ? null : MacDisplayIdentity.TryGetStableId(handle.Handle, handle.HandleDescriptor);
            },
            ReadmePath = Path.Combine(baseDirectory, "README.md"),
            StatusIcon = MacStatusIcon.Create(),
            Diagnostic = log.Write,
        };
    }
}
