using System.Collections.Concurrent;
using AiUsageMonitor.App.UI.Hosting;
using AiUsageMonitor.App.UI.Views;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Platform;
using AiUsageMonitor.Platform.Windows;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace AiUsageMonitor.App.Windows.Tests;

public sealed class WindowsLifecycleTests
{
    [AvaloniaFact]
    public async Task ProductLifecycleShowsTheWidgetOnActivationAndDisposesBeforeOsShutdown()
    {
        string root = await NewRootAsync();
        string activation = NewActivationName();
        try
        {
            int shutdowns = 0;
            var codes = new ConcurrentQueue<string>();
            var lifecycle = WindowsProductLifecycle.Start(() => shutdowns++, Services(root, codes, () => new FakeLayer()), activation);
            await WaitUntil(() => lifecycle.Host.Window is { IsVisible: true });

            // 二重起動の後続インスタンスからの通知で、隠していたウィジェットを再表示する。
            lifecycle.Host.Window!.Hide();
            Assert.True(WindowsInstanceActivationChannel.TrySignal(activation));
            await WaitUntil(() => lifecycle.Host.Window!.IsVisible);

            // OSからの終了要求は一度取り消し、hostの破棄後に改めて終了する。
            var request = new ShutdownRequestedEventArgs();
            lifecycle.OnShutdownRequested(request);
            Assert.True(request.Cancel);
            await WaitUntil(() => shutdowns == 1);
            Assert.False(WindowsInstanceActivationChannel.TrySignal(activation));
            var again = new ShutdownRequestedEventArgs();
            lifecycle.OnShutdownRequested(again);
            Assert.False(again.Cancel);
            await lifecycle.StopAsync();
            Assert.Equal(1, shutdowns);
            Assert.DoesNotContain("startup-failure", codes);
            Assert.Null(lifecycle.StartupFailureNotice);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task StartupFailureIsRecordedAndTheNoticeShutsDownWhenAcknowledged()
    {
        string root = await NewRootAsync();
        try
        {
            int shutdowns = 0;
            var codes = new ConcurrentQueue<string>();
            var lifecycle = WindowsProductLifecycle.Start(
                () => shutdowns++, Services(root, codes, () => throw new IOException("fixture")), NewActivationName());
            await WaitUntil(() => lifecycle.StartupFailureNotice is not null);
            Assert.Contains("startup-failure", codes);
            NoticeWindow notice = lifecycle.StartupFailureNotice!;
            Assert.Equal("AI Usage Monitorを起動できませんでした。設定とインストール先を確認してください。", notice.Message);
            Assert.Equal(0, shutdowns);

            // OKで閉じると、hostを破棄してから終了する。
            var ok = Assert.IsType<Button>(((StackPanel)notice.Content!).Children[1]);
            ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => shutdowns == 1);
            Assert.False(notice.IsVisible);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void NoticeRequiresAMessage()
    {
        Assert.Throws<ArgumentException>(() => new NoticeWindow(" "));
    }

    private static WidgetHostServices Services(string root, ConcurrentQueue<string> codes, Func<IWidgetLayerController> layer)
    {
        var paths = new WindowsAppPathProvider(root);
        return new WidgetHostServices
        {
            AppPaths = paths,
            Startup = new FakeStartup(),
            ShellOpener = new FakeShell(),
            CreateLayerController = layer,
            ClaudeSourceFactory = _ => new UnavailableClaudeUsageSource(),
            ClaudeWorkspace = new AppPathClaudeWorkspace(paths),
            ClaudeBridgePath = Path.Combine(root, "claude-statusline-bridge.ps1"),
            ReadmePath = Path.Combine(root, "README.md"),
            InheritedCodexHome = null,
            UserProfile = root,
            Diagnostic = (code, _) => codes.Enqueue(code),
        };
    }

    // 既存の設定を置き、ようこそ画面・CodexとClaudeの取得（実CLIの起動）を避ける。
    // 実際の%LOCALAPPDATA%、自動起動のregistry、配布物には触れない。
    private static async Task<string> NewRootAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "aiusage-windows-lifecycle-" + Guid.NewGuid().ToString("N"));
        using var store = new FileSystemSettingsStore(new WindowsAppPathProvider(root).SettingsFilePath);
        await store.SaveAsync(new AppSettings { ShowCodexUsage = false, ShowClaudeUsage = false });
        return root;
    }

    private static string NewActivationName() => "Local\\AiUsageMonitor-Test-" + Guid.NewGuid().ToString("N");

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    private sealed class FakeStartup : IStartupService
    {
        public void Apply(bool startWithSystem) { }
    }

    private sealed class FakeShell : IShellOpener
    {
        public void OpenFolder(string path) { }
    }

    private sealed class FakeLayer : IWidgetLayerController
    {
        public WidgetLayerHealth Health => new(false, null, 0);
        public event Action<WidgetLayerHealth>? HealthChanged { add { } remove { } }
        public void Attach(nint nativeWindowHandle) { }
        public void SetLayerMode(WidgetLayerMode mode) { }
        public void SetClickThrough(bool enabled) { }
        public bool TryRecoverLayer() => true;
        public void Dispose() { }
    }
}
