using System.Runtime.Versioning;
using AiUsageMonitor.App.UI.Hosting;
using AiUsageMonitor.Platform.Mac;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

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
        var demo = new DemoWidgetHost(desktop);
        demo.Start();
        desktop.Exit += (_, _) => demo.Dispose();
    }

    private static void StartProduct(IClassicDesktopStyleApplicationLifetime desktop)
    {
        MacAppPathProvider paths = Program.Paths;
        var log = new DiagnosticLog(paths.UserHomeDirectory);
        WidgetHost? host = null;
        bool stopped = false;
        // Codex app-serverの終了待ちを含む非同期の破棄を終えてから、lifetimeを終了する。
        // UIスレッドで破棄の完了を同期待ちすると、破棄中の継続がUIスレッドへ戻れずに止まる。
        async Task StopAsync(int exitCode)
        {
            if (stopped) return;
            stopped = true;
            try
            {
                if (host is not null) await host.DisposeAsync();
            }
            catch (Exception exception)
            {
                log.Write("host-dispose-failure", exception);
            }
            desktop.Shutdown(exitCode);
        }
        host = new WidgetHost(MacComposition.Create(paths, log), () => _ = StopAsync(0));
        desktop.ShutdownRequested += (_, e) =>
        {
            // ログアウト等、OSからの終了要求。破棄を済ませてから改めて終了する。
            if (stopped) return;
            e.Cancel = true;
            _ = StopAsync(0);
        };
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await host.StartAsync();
                log.Write("started", null);
            }
            catch (Exception exception)
            {
                log.Write("startup-failure", exception);
                var notice = new AiUsageMonitor.App.UI.Views.NoticeWindow(
                    $"AI Usage Monitorを起動できませんでした。設定ファイルが壊れているか、必要なリソースにアクセスできない可能性があります。\n\n{exception.GetType().Name}: {exception.Message}");
                notice.Closed += (_, _) => _ = StopAsync(1);
                notice.Show();
            }
        });
    }
}
