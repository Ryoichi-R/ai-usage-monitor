using System.Runtime.Versioning;
using AiUsageMonitor.App.UI.Hosting;
using AiUsageMonitor.Platform.Mac;

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
            // Claude active取得（CLIの/usage画面）はPhase 6で実装する。それまではCLIを起動しない。
            ClaudeSourceFactory = _ => new UnavailableClaudeUsageSource(),
            ClaudeWorkspace = new AppPathClaudeWorkspace(paths),
            ClaudeBridgePath = Path.Combine(baseDirectory, "claude-statusline-bridge"),
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
