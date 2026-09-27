namespace AiUsageMonitor.Platform.Mac.Tests;

public sealed class MacWidgetLayerControllerTests
{
    [Fact]
    public void AttachAddsWidgetSpaceBehaviorAndAppliesTheRequestedLayer()
    {
        var api = new FakeApi { CollectionBehavior = 1UL << 10 };
        using var controller = new MacWidgetLayerController(api);

        controller.SetLayerMode(WidgetLayerMode.AlwaysOnTop);
        controller.SetClickThrough(true);
        Assert.Equal(0, api.SetLevelCalls);

        controller.Attach(FakeApi.ViewHandle);

        Assert.Equal((1UL << 10) | MacWidgetLayerController.WidgetCollectionBehavior, api.CollectionBehavior);
        Assert.Equal(api.LevelFor(WidgetLayerMode.AlwaysOnTop), api.Level);
        Assert.True(api.IgnoresMouseEvents);
        Assert.False(controller.Health.IsDegraded);
    }

    [Theory]
    [InlineData(WidgetLayerMode.Normal)]
    [InlineData(WidgetLayerMode.AlwaysOnTop)]
    [InlineData(WidgetLayerMode.AlwaysOnBottom)]
    public void LayerModeAndClickThroughAreReadBackAfterEachChange(WidgetLayerMode mode)
    {
        var api = new FakeApi();
        using var controller = new MacWidgetLayerController(api);
        controller.Attach(FakeApi.WindowHandle);

        controller.SetLayerMode(mode);
        controller.SetClickThrough(true);
        controller.SetClickThrough(false);

        Assert.Equal(api.LevelFor(mode), api.Level);
        Assert.False(api.IgnoresMouseEvents);
        Assert.False(controller.Health.IsDegraded);
    }

    [Fact]
    public void MismatchedReadBackIsDegradedAndRecoveryReappliesTheLayer()
    {
        var api = new FakeApi { IgnoreLevelWrites = true };
        using var controller = new MacWidgetLayerController(api);
        var health = new List<WidgetLayerHealth>();
        controller.HealthChanged += health.Add;
        controller.Attach(FakeApi.WindowHandle);

        controller.SetLayerMode(WidgetLayerMode.AlwaysOnBottom);
        Assert.True(controller.Health.IsDegraded);
        Assert.Equal(nameof(MacWidgetLayerController.SetLayerMode), controller.Health.Operation);
        Assert.False(controller.TryRecoverLayer());

        api.IgnoreLevelWrites = false;
        Assert.True(controller.TryRecoverLayer());
        Assert.Equal(api.LevelFor(WidgetLayerMode.AlwaysOnBottom), api.Level);
        Assert.Equal([true, false], health.Select(value => value.IsDegraded));

        int writes = api.SetLevelCalls;
        Assert.True(controller.TryRecoverLayer());
        Assert.Equal(writes, api.SetLevelCalls);
    }

    [Fact]
    public void UnresolvableHandleIsReportedAndLeavesTheControllerDetached()
    {
        var api = new FakeApi();
        using var controller = new MacWidgetLayerController(api);

        controller.Attach(1234);
        controller.SetLayerMode(WidgetLayerMode.AlwaysOnTop);

        Assert.True(controller.Health.IsDegraded);
        Assert.Equal(nameof(MacWidgetLayerController.Attach), controller.Health.Operation);
        Assert.Equal(0, api.SetLevelCalls);
        Assert.False(controller.TryRecoverLayer());
    }

    [Fact]
    public void DisposeDetachesTheWindow()
    {
        var api = new FakeApi();
        var controller = new MacWidgetLayerController(api);
        controller.Attach(FakeApi.WindowHandle);
        int writes = api.SetLevelCalls;

        controller.Dispose();
        controller.SetLayerMode(WidgetLayerMode.AlwaysOnTop);

        Assert.Equal(writes, api.SetLevelCalls);
    }

    [Fact]
    public void ShellOpenerRejectsEmptyPaths()
    {
        var opener = new MacShellOpener();
        Assert.Throws<ArgumentException>(() => opener.OpenFolder(" "));
    }

    private sealed class FakeApi : INativeWindowLayerApi
    {
        public const nint WindowHandle = 100;
        public const nint ViewHandle = 200;

        public long Level { get; private set; }
        public bool IgnoresMouseEvents { get; private set; }
        public ulong CollectionBehavior { get; set; }
        public bool IgnoreLevelWrites { get; set; }
        public int SetLevelCalls { get; private set; }

        public nint ResolveWindow(nint handle) => handle switch
        {
            WindowHandle => WindowHandle,
            ViewHandle => WindowHandle,
            _ => 0,
        };

        public long LevelFor(WidgetLayerMode mode) => mode switch
        {
            WidgetLayerMode.AlwaysOnTop => 3,
            WidgetLayerMode.AlwaysOnBottom => -2147483623,
            _ => 0,
        };

        public long GetLevel(nint window) => Level;

        public void SetLevel(nint window, long level)
        {
            Assert.Equal(WindowHandle, window);
            SetLevelCalls++;
            if (!IgnoreLevelWrites) Level = level;
        }

        public bool GetIgnoresMouseEvents(nint window) => IgnoresMouseEvents;

        public void SetIgnoresMouseEvents(nint window, bool value) => IgnoresMouseEvents = value;

        public ulong GetCollectionBehavior(nint window) => CollectionBehavior;

        public void SetCollectionBehavior(nint window, ulong value) => CollectionBehavior = value;
    }
}
