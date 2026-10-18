using AiUsageMonitor.App.UI.Views;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Platform;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace AiUsageMonitor.App.UI.Tests;

// WPF版BackgroundWindowの層維持（最背面・クリック透過・失敗時の背景抑止）をfake層制御で検証する。
public sealed class BackgroundWindowTests
{
    [AvaloniaFact]
    public void PresentationAttachesAsClickThroughBottomLayerAndHidesWhenNotRequested()
    {
        var layer = new FakeLayer();
        var window = new BackgroundWindow(layer, (_, _) => { }) { HandleOverrideForTest = 42 };
        try
        {
            window.ApplyAppearance(new AppSettings { BackgroundEnabled = true, Opacity = 0.8 });
            window.SetPresentationRequested(true);
            Dispatcher.UIThread.RunJobs();

            Assert.True(window.IsVisible);
            Assert.Equal(42, layer.AttachedHandle);
            Assert.True(layer.ClickThrough);
            Assert.Equal(WidgetLayerMode.AlwaysOnBottom, layer.Mode);
            Assert.Equal(0.8, window.Opacity, 3);

            window.SetPresentationRequested(false);
            Assert.False(window.IsVisible);
            Assert.False(window.IsPresentationRequested);
            window.SetPresentationRequested(true);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);
            Assert.Equal(1, layer.AttachCalls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DegradedLayerSuppressesTheBackgroundUntilRepairRecovers()
    {
        var layer = new FakeLayer();
        var diagnostics = new List<string>();
        var window = new BackgroundWindow(layer, (code, _) => diagnostics.Add(code)) { HandleOverrideForTest = 42 };
        try
        {
            window.ApplyAppearance(new AppSettings { Opacity = 0.6 });
            window.SetPresentationRequested(true);
            Dispatcher.UIThread.RunJobs();

            layer.SetHealth(new(true, "SetLayerMode", 5));
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.LayerFailureSuppressed);
            Assert.Equal(0, window.Opacity);

            // 抑止中の外観変更でも背景は隠したまま。
            window.ApplyAppearance(new AppSettings { Opacity = 0.9 });
            Assert.Equal(0, window.Opacity);

            layer.RecoverOnNextRepair = true;
            window.RunRepairTick();
            Assert.Equal(1, layer.RecoverCalls);
            Assert.False(window.LayerFailureSuppressed);
            Assert.Equal(0.9, window.Opacity, 3);
            Assert.Equal(["background-layer-suppressed", "background-layer-recovered"], diagnostics);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SuspendedRepairDoesNotTouchTheLayerAndResumeReapplies()
    {
        var layer = new FakeLayer();
        var window = new BackgroundWindow(layer, (_, _) => { }) { HandleOverrideForTest = 42 };
        try
        {
            window.SetPresentationRequested(true);
            Dispatcher.UIThread.RunJobs();
            int applied = layer.ModeCalls;

            window.SetRepairSuspended(true);
            window.RunRepairTick();
            Assert.Equal(0, layer.RecoverCalls);

            window.SetRepairSuspended(false);
            Assert.Equal(applied + 1, layer.ModeCalls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void LayerFailuresAreReportedWithoutThrowing()
    {
        var layer = new FakeLayer { ThrowOnMode = true, ThrowOnRecover = true };
        var diagnostics = new List<string>();
        var window = new BackgroundWindow(layer, (code, _) => diagnostics.Add(code)) { HandleOverrideForTest = 42 };
        try
        {
            window.SetPresentationRequested(true);
            Dispatcher.UIThread.RunJobs();
            window.RunRepairTick();

            Assert.Contains("background-layer-apply-failure", diagnostics);
            Assert.Contains("background-layer-repair-failure", diagnostics);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void WithoutALayerControllerTheWindowStillPresentsTheBackground()
    {
        var window = new BackgroundWindow();
        try
        {
            window.ApplyAppearance(new AppSettings { BackgroundEnabled = true });
            window.SetPresentationRequested(true);
            window.RunRepairTick();
            Assert.True(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class FakeLayer : IWidgetLayerController
    {
        public WidgetLayerHealth Health { get; private set; } = new(false, null, 0);

        public nint AttachedHandle { get; private set; }

        public WidgetLayerMode Mode { get; private set; }

        public int ModeCalls { get; private set; }

        public bool ClickThrough { get; private set; }

        public int RecoverCalls { get; private set; }

        public bool RecoverOnNextRepair { get; set; }

        public bool ThrowOnMode { get; init; }

        public bool ThrowOnRecover { get; init; }

        public event Action<WidgetLayerHealth>? HealthChanged;

        public int AttachCalls { get; private set; }
        public void Attach(nint nativeWindowHandle)
        {
            if (++AttachCalls > 1) throw new InvalidOperationException("Already attached to a window handle.");
            AttachedHandle = nativeWindowHandle;
        }

        public void SetLayerMode(WidgetLayerMode mode)
        {
            ModeCalls++;
            if (ThrowOnMode) throw new InvalidOperationException("mode");
            Mode = mode;
        }

        public void SetClickThrough(bool enabled) => ClickThrough = enabled;

        public bool TryRecoverLayer()
        {
            RecoverCalls++;
            if (ThrowOnRecover) throw new InvalidOperationException("recover");
            if (RecoverOnNextRepair) Health = new(false, null, 0);
            return !Health.IsDegraded;
        }

        public void SetHealth(WidgetLayerHealth health)
        {
            Health = health;
            HealthChanged?.Invoke(health);
        }

        public void Dispose()
        {
        }
    }
}
