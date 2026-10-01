using AiUsageMonitor.App.UI.Hosting;
using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Claude.Transport;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;
using AiUsageMonitor.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using PlacementMode = AiUsageMonitor.Core.Settings.PlacementMode;

namespace AiUsageMonitor.App.UI.Tests;

public sealed class StartupPlacementTests
{
    [AvaloniaTheory]
    [InlineData(WidgetDisplayMode.Compact, 100)]
    [InlineData(WidgetDisplayMode.Compact, 150)]
    [InlineData(WidgetDisplayMode.Standard, 100)]
    public async Task CustomPositionUsesSettledInitialLayout(WidgetDisplayMode mode, double scale)
    {
        await using var fixture = await Fixture.CreateAsync(mode, scale);
        await fixture.Host.StartAsync();
        var window = fixture.Host.Window!;
        MainWindowTestSupport.Settle(window);
        Assert.Equal(ExpectedPosition(fixture.Host), window.Position);
        using var stored = new FileSystemSettingsStore(fixture.Paths.SettingsFilePath);
        Assert.Equal(fixture.Saved.CustomTopFraction, (await stored.LoadAsync()).CustomTopFraction);
    }

    [AvaloniaFact]
    public async Task CustomPositionUsesLayoutAfterInitialUsageArrives()
    {
        var source = new DelayedSource();
        await using var fixture = await Fixture.CreateAsync(WidgetDisplayMode.Compact, 100, source);
        Task starting = fixture.Host.StartAsync();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var window = fixture.Host.Window!;
        MainWindowTestSupport.Settle(window);
        double initialHeight = window.GetInformationGeometry().HeightDip;
        source.Completion.SetResult(new(ClaudeUsageSourceKind.CliScreen,
            UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow) with
            {
                Availability = UsageAvailability.Available,
                Windows = [new("weekly", "weekly limit", "secondary", 25, 10080, DateTimeOffset.UtcNow.AddDays(3), null)],
                LastSuccessfulAt = DateTimeOffset.UtcNow,
            }));
        await starting;
        MainWindowTestSupport.Settle(window);
        Assert.NotEqual(initialHeight, window.GetInformationGeometry().HeightDip);
        Assert.Equal(ExpectedPosition(fixture.Host), window.Position);
    }

    private static PixelPoint ExpectedPosition(WidgetHost host)
    {
        var screen = host.Window!.Screens.Primary!;
        var info = new ScreenInfo(screen.DisplayName ?? "primary", null, true, screen.WorkingArea, screen.Scaling);
        return WidgetScreenPlacement.Calculate(info, host.Window.GetInformationGeometry(), host.Settings);
    }

    [AvaloniaFact]
    public async Task LaterContentShrinkKeepsCustomTopLeft()
    {
        var source = new DelayedSource();
        await using var fixture = await Fixture.CreateAsync(WidgetDisplayMode.Compact, 100, source);
        Task starting = fixture.Host.StartAsync();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.Completion.SetResult(AvailableObservation());
        await starting;
        var window = fixture.Host.Window!;
        MainWindowTestSupport.Settle(window);
        PixelPoint settledPosition = window.Position;
        double settledHeight = window.GetInformationGeometry().HeightDip;
        fixture.Host.ViewModel!.Apply(AvailableObservation().Snapshot with { Windows = [] });
        window.RepositionAfterContentChange();
        MainWindowTestSupport.Settle(window);
        Assert.True(window.GetInformationGeometry().HeightDip < settledHeight);
        Assert.Equal(settledPosition, window.Position);
    }

    [AvaloniaFact]
    public async Task UserMoveCancelsInitialRestore()
    {
        var source = new DelayedSource();
        await using var fixture = await Fixture.CreateAsync(WidgetDisplayMode.Compact, 100, source);
        Task starting = fixture.Host.StartAsync();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var window = fixture.Host.Window!;
        MainWindowTestSupport.Settle(window);
        Assert.True(window.TryBeginUserMove());
        var desired = new PixelPoint(300, 300);
        window.Position = desired;
        window.CompleteUserMove();
        source.Completion.SetResult(AvailableObservation());
        await starting;
        MainWindowTestSupport.Settle(window);
        Assert.Equal(desired, window.Position);
        Assert.NotEqual(fixture.Saved.CustomTopFraction, fixture.Host.Settings.CustomTopFraction);
    }

    private static ClaudeUsageObservation AvailableObservation() => new(ClaudeUsageSourceKind.CliScreen,
        UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow) with
        {
            Availability = UsageAvailability.Available,
            Windows = [new("weekly", "weekly limit", "secondary", 25, 10080, DateTimeOffset.UtcNow.AddDays(3), null)],
            LastSuccessfulAt = DateTimeOffset.UtcNow,
        });

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string root, TestPaths paths, WidgetHost host, AppSettings saved)
            => (Root, Paths, Host, Saved) = (root, paths, host, saved);
        private string Root { get; }
        public TestPaths Paths { get; }
        public WidgetHost Host { get; }
        public AppSettings Saved { get; }

        public static async Task<Fixture> CreateAsync(WidgetDisplayMode mode, double scale, DelayedSource? source = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "aiusage-placement-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var paths = new TestPaths(root);
            var settings = new AppSettings
            {
                ShowCodexUsage = false,
                ShowClaudeUsage = source is not null,
                ClaudeSetupCompleted = true,
                ClickThrough = false,
                PlacementMode = PlacementMode.Custom,
                Anchor = PlacementAnchor.BottomRight,
                CustomLeftFraction = 1,
                CustomTopFraction = 0.996268656716418,
                DisplayMode = mode,
                UiScalePercent = scale,
            };
            using (var store = new FileSystemSettingsStore(paths.SettingsFilePath)) await store.SaveAsync(settings);
            var services = new WidgetHostServices
            {
                AppPaths = paths,
                Startup = new NoStartup(),
                ShellOpener = new NoShell(),
                CreateLayerController = () => new NoLayer(),
                ClaudeSourceFactory = _ => source is null ? new UnavailableClaudeUsageSource() : source,
                ClaudeWorkspace = new AppPathClaudeWorkspace(paths),
                ClaudeBridgePath = Path.Combine(root, "bridge"),
                ReadmePath = Path.Combine(root, "README.md"),
                InheritedCodexHome = null,
                UserProfile = root,
            };
            return new(root, paths, new WidgetHost(services, () => { }), settings);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class DelayedSource : IClaudeUsageSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ClaudeUsageObservation> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ClaudeUsageObservation> RefreshAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return Completion.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class TestPaths(string root) : IAppPathProvider
    {
        public string SettingsFilePath => Path.Combine(root, "settings.json");
        public string ClaudeWorkspaceDirectory => Path.Combine(root, "workspace");
        public string TemporaryDirectory => root;
        public string UserHomeDirectory => root;
    }
    private sealed class NoStartup : IStartupService { public void Apply(bool startWithSystem) { } }
    private sealed class NoShell : IShellOpener { public void OpenFolder(string path) { } }
    private sealed class NoLayer : IWidgetLayerController
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
