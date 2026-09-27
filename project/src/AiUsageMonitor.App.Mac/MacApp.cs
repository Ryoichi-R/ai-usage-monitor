using System.Runtime.Versioning;
using AiUsageMonitor.Platform.Mac;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace AiUsageMonitor.App.Mac;

[SupportedOSPlatform("macos")]
internal sealed class MacApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Default;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (Program.DemoMode)
                StartDemo(desktop);
            else
                StartProduct(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void StartDemo(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var demo = new DemoWidgetHost(() => desktop.Shutdown());
        demo.Start();
        desktop.Exit += (_, _) => demo.Dispose();
    }

    private static void StartProduct(IClassicDesktopStyleApplicationLifetime desktop)
    {
        MacAppPathProvider paths = Program.Paths;
        var log = new DiagnosticLog(paths.UserHomeDirectory);
        var lifecycle = MacProductLifecycle.Start(desktop.Shutdown, MacComposition.Create(paths, log), log.Write);
        desktop.ShutdownRequested += (_, e) => lifecycle.OnShutdownRequested(e);
    }
}
