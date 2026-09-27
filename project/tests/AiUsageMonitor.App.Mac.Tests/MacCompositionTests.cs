using System.Runtime.Versioning;
using AiUsageMonitor.Platform;
using AiUsageMonitor.Core.Settings;
using Avalonia.Threading;
using AiUsageMonitor.App.UI.Hosting;
using AiUsageMonitor.App.UI.Runtime;
using AiUsageMonitor.Core.Usage;
using AiUsageMonitor.Platform.Mac;
using Avalonia.Headless.XUnit;

namespace AiUsageMonitor.App.Mac.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacCompositionTests
{
    [AvaloniaFact]
    public async Task ProductCompositionRequiresSupervisedClaudeAndPassiveListener()
    {
        string root = Path.Combine(Path.GetTempPath(), "aiusage-composition-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new MacAppPathProvider(root);
            var log = new DiagnosticLog(root);
            WidgetHostServices services = MacComposition.Create(paths, log);
            Assert.IsType<MacManagedProcessLauncher>(services.CodexProcessLauncher);
            Assert.Null(services.CodexLifetimeGuardFactory);
            Assert.NotNull(services.CreateClaudeListener);
            // 生成だけでは受信口を開かない（socketはhostのStartで作る）。
            Assert.IsType<AiUsageMonitor.Claude.Mac.ClaudeUsageSocketListener>(services.CreateClaudeListener());
            // 自動起動は明示したhome配下のLaunchAgentsだけへ書き、解除で消す。
            services.Startup.Apply(true);
            string plist = Assert.Single(Directory.GetFiles(paths.LaunchAgentsDirectory, "*.plist"));
            Assert.Contains(Environment.ProcessPath!, File.ReadAllText(plist), StringComparison.Ordinal);
            services.Startup.Apply(false);
            Assert.False(File.Exists(plist));
            Assert.NotNull(services.ClaudeSetupExample);
            var source = services.ClaudeSourceFactory(new ClaudeActiveSourceConfiguration(Path.Combine(root, "missing-claude"), "bridge", TimeSpan.FromSeconds(1)));
            var observation = await source.RefreshAsync(TestContext.Current.CancellationToken);
            Assert.Equal(UsageAvailability.NotInstalled, observation.Snapshot.Availability);
            Assert.NotNull(services.StatusIcon);
            Assert.Same(paths, services.AppPaths);
            services.Diagnostic("test-code", new IOException("must-not-be-logged"));
            string content = File.ReadAllText(log.FilePath);
            Assert.Contains("test-code IOException", content, StringComparison.Ordinal);
            Assert.DoesNotContain("must-not-be-logged", content, StringComparison.Ordinal);
            Assert.True(DemoUsageData.Create().ShowCodex);
            using var layer = services.CreateLayerController();
            Assert.IsType<MacWidgetLayerController>(layer);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public void DemoHostChangesPresentationWithoutStartingProvidersOrSavingSettings()
    {
        var layer = new FakeLayer();
        using var host = new DemoWidgetHost(() => { }, layer);
        host.Start();
        Dispatcher.UIThread.RunJobs();
        Assert.True(host.Window!.IsVisible);
        Assert.Equal(WidgetLayerMode.AlwaysOnTop, layer.Mode);
        Assert.True(layer.ClickThrough);
        host.Apply(new AppSettings
        {
            ShowClaudeUsage = true,
            DisplayMode = WidgetDisplayMode.Compact,
            UiScalePercent = 150,
            AlwaysOnTop = false,
            ClickThrough = false
        });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(150, host.Window.CurrentBaseWidgetWidthDip);
        Assert.Equal(WidgetLayerMode.Normal, layer.Mode);
        Assert.False(layer.ClickThrough);
        host.Apply(new AppSettings
        {
            ShowClaudeUsage = true,
            BackgroundEnabled = true,
            BackgroundFillMode = BackgroundFillMode.EdgeFade,
            UiScalePercent = 200
        });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(280, host.Window.CurrentBaseWidgetWidthDip);
        host.Dispose();
        Assert.True(layer.Disposed);
        Assert.False(host.Window.IsVisible);
    }

    private sealed class FakeLayer : IWidgetLayerController
    {
        public WidgetLayerHealth Health => new(false, null, 0);
        public event Action<WidgetLayerHealth>? HealthChanged { add { } remove { } }
        public WidgetLayerMode Mode { get; private set; }
        public bool ClickThrough { get; private set; }
        public bool Disposed { get; private set; }
        public void Attach(nint nativeWindowHandle) { }
        public void SetLayerMode(WidgetLayerMode mode) => Mode = mode;
        public void SetClickThrough(bool enabled) => ClickThrough = enabled;
        public bool TryRecoverLayer() => true;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void DiagnosticRotationKeepsOneGenerationAndSurvivesIoFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "aiusage-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new DiagnosticLog(root);
            log.Write("initial", null);
            File.WriteAllText(log.FilePath, new string('x', 1024 * 1024 + 1));
            log.Write("rotated", null);
            Assert.True(File.Exists(log.FilePath + ".1"));
            Assert.Contains("rotated", File.ReadAllText(log.FilePath), StringComparison.Ordinal);
            File.Delete(log.FilePath);
            Directory.CreateDirectory(log.FilePath);
            log.Write("ignored-io-failure", null);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
