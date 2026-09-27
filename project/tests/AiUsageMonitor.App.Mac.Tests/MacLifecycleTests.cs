using System.Runtime.Versioning;
using AiUsageMonitor.App.UI.Hosting;
using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Platform;
using AiUsageMonitor.Platform.Mac;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace AiUsageMonitor.App.Mac.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacLifecycleTests
{
    [Fact]
    public void RunStartsOnlyOneProductInstanceAndDemoSkipsTheLock()
    {
        string root = NewRoot();
        try
        {
            var paths = new MacAppPathProvider(root);
            int nested = -1;
            int result = Program.Run([], paths, _ =>
            {
                // 起動中は同じlockを取れないため、二つ目は起動せずに0で終わる。
                nested = Program.Run([], paths, _ => throw new InvalidOperationException("second instance started"));
                return 7;
            });
            Assert.Equal(7, result);
            Assert.Equal(0, nested);
            Assert.False(Program.DemoMode);
            Assert.Equal(3, Program.Run(["--demo"], new MacAppPathProvider(Path.Combine(root, "unused")), _ => 3));
            Assert.True(Program.DemoMode);
            Assert.False(Directory.Exists(Path.Combine(root, "unused")));
            Assert.Equal(5, Program.Run([], paths, _ => 5));
            Assert.NotNull(Program.BuildAvaloniaApp());
        }
        finally
        {
            Program.Run([], new MacAppPathProvider(root), _ => 0);
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task ProductLifecycleStartsHostAndDisposesBeforeOsShutdown()
    {
        string root = NewRoot();
        try
        {
            var lifetime = new FakeLifetime();
            var codes = new List<string>();
            var lifecycle = MacProductLifecycle.Start(lifetime.Shutdown, Services(root, () => new FakeLayer()), (code, _) => codes.Add(code));
            Dispatcher.UIThread.RunJobs();
            await WaitUntil(() => codes.Contains("started"));
            Assert.True(lifecycle.Host.Window!.IsVisible);

            // OSからの終了要求は一度取り消し、hostの破棄後に改めて終了する。
            var request = new ShutdownRequestedEventArgs();
            lifecycle.OnShutdownRequested(request);
            Assert.True(request.Cancel);
            await WaitUntil(() => lifetime.ExitCode is not null);
            Assert.Equal(0, lifetime.ExitCode);
            var again = new ShutdownRequestedEventArgs();
            lifecycle.OnShutdownRequested(again);
            Assert.False(again.Cancel);
            await lifecycle.StopAsync(1);
            Assert.Equal(1, lifetime.ShutdownCalls);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task StartupFailureShowsNoticeAndExitsWithErrorWhenClosed()
    {
        string root = NewRoot();
        try
        {
            var lifetime = new FakeLifetime();
            var codes = new List<string>();
            var lifecycle = MacProductLifecycle.Start(lifetime.Shutdown, Services(root, () => throw new IOException("fixture")), (code, _) => codes.Add(code));
            Dispatcher.UIThread.RunJobs();
            await WaitUntil(() => lifecycle.StartupFailureNotice is not null);
            Assert.Contains("startup-failure", codes);
            lifecycle.StartupFailureNotice!.Close();
            await WaitUntil(() => lifetime.ExitCode is not null);
            Assert.Equal(1, lifetime.ExitCode);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void DemoMenuDrivesLayerAndDisplayWithoutSavingSettings()
    {
        var layer = new FakeLayer();
        var lifetime = new FakeLifetime();
        using var host = new DemoWidgetHost(() => lifetime.Shutdown(0), layer);
        host.Start();
        Dispatcher.UIThread.RunJobs();
        NativeMenuItem[] items = Flatten(host.TrayMenu!).ToArray();
        NativeMenuItem Find(string header) => items.Single(item => item.Header == header);

        Click(Find("常に手前に表示"));
        Assert.Equal(WidgetLayerMode.Normal, layer.Mode);
        Click(Find("デスクトップ最背面"));
        Assert.Equal(WidgetLayerMode.AlwaysOnBottom, layer.Mode);
        Assert.True(Find("デスクトップ最背面").IsChecked);
        Click(Find("デスクトップ最背面"));
        Assert.Equal(WidgetLayerMode.Normal, layer.Mode);
        Click(Find("クリックを透過"));
        Assert.False(layer.ClickThrough);
        foreach (string header in new[] { "縮小表示", "標準表示", "倍率 150%", "背景 単色", "背景 端を透明化", "背景なし", "設定…", "ようこそ画面…", "Claude Code連携…" })
            Click(Find(header));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, lifetime.ShutdownCalls);
        Click(Find("終了"));
        Assert.Equal(1, lifetime.ShutdownCalls);
    }

    private static IEnumerable<NativeMenuItem> Flatten(NativeMenu menu)
    {
        foreach (NativeMenuItem item in menu.Items.OfType<NativeMenuItem>())
        {
            yield return item;
            if (item.Menu is { } child)
                foreach (NativeMenuItem nested in Flatten(child)) yield return nested;
        }
    }

    private static void Click(NativeMenuItem item) => ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();

    private static WidgetHostServices Services(string root, Func<IWidgetLayerController> layer)
    {
        var paths = new MacAppPathProvider(root);
        return new WidgetHostServices
        {
            AppPaths = paths,
            Startup = new LaunchAgentStartupService(paths.LaunchAgentsDirectory, () => ["/fixture/monitor"]),
            ShellOpener = new MacShellOpener(_ => null),
            CreateLayerController = layer,
            ClaudeSourceFactory = _ => new UnavailableClaudeUsageSource(),
            ClaudeWorkspace = new AppPathClaudeWorkspace(paths),
            ClaudeBridgePath = Path.Combine(root, "bridge"),
            ReadmePath = Path.Combine(root, "README.md"),
            InheritedCodexHome = null,
            UserProfile = root,
        };
    }

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

    private static string NewRoot()
    {
        string root = Path.Combine("/private/tmp", "aiusage-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return root;
    }

    private sealed class FakeLayer : IWidgetLayerController
    {
        public WidgetLayerHealth Health => new(false, null, 0);
        public event Action<WidgetLayerHealth>? HealthChanged { add { } remove { } }
        public WidgetLayerMode Mode { get; private set; }
        public bool ClickThrough { get; private set; }
        public void Attach(nint nativeWindowHandle) { }
        public void SetLayerMode(WidgetLayerMode mode) => Mode = mode;
        public void SetClickThrough(bool enabled) => ClickThrough = enabled;
        public bool TryRecoverLayer() => true;
        public void Dispose() { }
    }

    private sealed class FakeLifetime
    {
        public int? ExitCode { get; private set; }
        public int ShutdownCalls { get; private set; }
        public void Shutdown(int exitCode) { ShutdownCalls++; ExitCode = exitCode; }
    }
}
