using System.Diagnostics.CodeAnalysis;
using AiUsageMonitor.App.UI.Hosting;
using AiUsageMonitor.App.UI.Views;
using AiUsageMonitor.Platform.Windows;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace AiUsageMonitor.App.Windows;

/// <summary>製品起動時のhostの開始・再表示通知・終了とlifetimeの終了順序を所有する。</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "StopAsync disposes the host and the activation channel before shutting down the lifetime.")]
internal sealed class WindowsProductLifecycle
{
    private readonly Action _shutdown;
    private readonly Action<string, Exception?> _log;
    private readonly WidgetHost _host;
    private readonly WindowsInstanceActivationChannel _activation;
    private bool _stopping;

    private WindowsProductLifecycle(Action shutdown, WidgetHostServices services, string activationName)
    {
        _shutdown = shutdown;
        _log = services.Diagnostic;
        _host = new WidgetHost(services, () => _ = StopAsync());
        // 後続の起動（二重起動）から届く通知で、既存のウィジェットを再表示する。
        _activation = WindowsInstanceActivationChannel.Listen(activationName,
            () => Dispatcher.UIThread.Post(_host.ShowWidget));
    }

    /// <summary>起動に失敗したときに表示した通知。テストで閉じる操作を再現するために公開する。</summary>
    internal NoticeWindow? StartupFailureNotice { get; private set; }

    internal WidgetHost Host => _host;

    /// <param name="shutdown">lifetimeを終了する操作。hostの破棄後に一度だけ呼ぶ。</param>
    internal static WindowsProductLifecycle Start(Action shutdown, WidgetHostServices services, string activationName)
    {
        var lifecycle = new WindowsProductLifecycle(shutdown, services, activationName);
        Dispatcher.UIThread.Post(async () => await lifecycle.StartHostAsync());
        return lifecycle;
    }

    /// <summary>サインアウト等、OSからの終了要求。一度取り消し、hostの破棄を済ませてから改めて終了する。</summary>
    internal void OnShutdownRequested(ShutdownRequestedEventArgs request)
    {
        if (_stopping) return;
        request.Cancel = true;
        _ = StopAsync();
    }

    private async Task StartHostAsync()
    {
        try
        {
            await _host.StartAsync();
        }
        catch (Exception exception)
        {
            _log("startup-failure", exception);
            var notice = new NoticeWindow("AI Usage Monitorを起動できませんでした。設定とインストール先を確認してください。");
            notice.Closed += (_, _) => _ = StopAsync();
            StartupFailureNotice = notice;
            notice.Show();
        }
    }

    // Codex app-serverの終了待ちを含む非同期の破棄を終えてから、lifetimeを終了する。
    internal async Task StopAsync()
    {
        if (_stopping) return;
        _stopping = true;
        try
        {
            await _host.DisposeAsync();
        }
        catch (Exception exception)
        {
            _log("host-dispose-failure", exception);
        }
        finally
        {
            _activation.Dispose();
            _shutdown();
        }
    }
}
