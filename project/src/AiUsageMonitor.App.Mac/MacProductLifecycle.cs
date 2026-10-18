using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using AiUsageMonitor.App.UI.Hosting;
using AiUsageMonitor.App.UI.Views;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace AiUsageMonitor.App.Mac;

/// <summary>製品起動時のhostの開始・終了とlifetimeの終了順序を所有する。</summary>
[SupportedOSPlatform("macos")]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "StopAsync disposes the host before shutting down the lifetime.")]
internal sealed class MacProductLifecycle
{
    private readonly Action<int> _shutdown;
    private readonly Action<string, Exception?> _log;
    private readonly WidgetHost _host;
    private bool _stopped;

    private MacProductLifecycle(Action<int> shutdown, WidgetHostServices services, Action<string, Exception?> log)
    {
        _shutdown = shutdown;
        _log = log;
        _host = new WidgetHost(services, () => _ = StopAsync(0));
    }

    /// <summary>起動に失敗したときに表示した通知。テストで閉じる操作を再現するために公開する。</summary>
    internal NoticeWindow? StartupFailureNotice { get; private set; }

    internal WidgetHost Host => _host;

    /// <param name="shutdown">lifetimeを終了する操作。hostの破棄後に一度だけ呼ぶ。</param>
    internal static MacProductLifecycle Start(Action<int> shutdown, WidgetHostServices services, Action<string, Exception?> log)
    {
        var lifecycle = new MacProductLifecycle(shutdown, services, log);
        Dispatcher.UIThread.Post(async () => await lifecycle.StartHostAsync());
        return lifecycle;
    }

    /// <summary>ログアウト等、OSからの終了要求。一度取り消し、hostの破棄を済ませてから改めて終了する。</summary>
    internal void OnShutdownRequested(ShutdownRequestedEventArgs request)
    {
        if (_stopped) return;
        request.Cancel = true;
        _ = StopAsync(0);
    }

    private async Task StartHostAsync()
    {
        try
        {
            await _host.StartAsync();
            _log("started", null);
        }
        catch (Exception exception)
        {
            _log("startup-failure", exception);
            var notice = new NoticeWindow(
                $"AI Usage Monitorを起動できませんでした。設定ファイルが壊れているか、必要なリソースにアクセスできない可能性があります。\n\n{exception.GetType().Name}: {exception.Message}");
            notice.Closed += (_, _) => _ = StopAsync(1);
            StartupFailureNotice = notice;
            notice.Show();
        }
    }

    // Codex app-serverの終了待ちを含む非同期の破棄を終えてから、lifetimeを終了する。
    // UIスレッドで破棄の完了を同期待ちすると、破棄中の継続がUIスレッドへ戻れずに止まる。
    internal async Task StopAsync(int exitCode)
    {
        if (_stopped) return;
        _stopped = true;
        try
        {
            await _host.DisposeAsync();
        }
        catch (Exception exception)
        {
            _log("host-dispose-failure", exception);
        }
        _shutdown(exitCode);
    }
}
