using System.Runtime.InteropServices;
using AiUsageMonitor.Windows.Window;

namespace AiUsageMonitor.Windows.Tests;

public sealed class WindowLayerTests
{
    [Fact]
    public void BottomMostApplyUsesExpectedInsertAfterAndFlagsForEachStrategy()
    {
        var api = new FakeWindowLayerApi();

        Assert.True(BottomMostStrategy.Apply(new nint(10), LayerStrategy.BottomMost, api));
        Assert.Equal(WindowInterop.HWND_BOTTOM, api.LastInsertAfter);
        Assert.Equal(WindowInterop.SWP_NOMOVE | WindowInterop.SWP_NOSIZE | WindowInterop.SWP_NOACTIVATE, api.LastFlags);
        Assert.True(BottomMostStrategy.Apply(new nint(10), LayerStrategy.TopMost, api));
        Assert.Equal(WindowInterop.HWND_TOPMOST, api.LastInsertAfter);
        Assert.True(BottomMostStrategy.Apply(new nint(10), LayerStrategy.Normal, api));
        Assert.Equal(WindowInterop.HWND_NOTOPMOST, api.LastInsertAfter);
    }

    [Fact]
    public void BottomMostApplyHandlesInvalidHandleFailureAndException()
    {
        var api = new FakeWindowLayerApi { ThrowOnSet = true };

        Assert.False(BottomMostStrategy.Apply(0, LayerStrategy.BottomMost, api));
        Assert.False(BottomMostStrategy.Apply(new nint(10), LayerStrategy.BottomMost, api));
        api.ThrowOnSet = false;
        api.SetSucceeded = false;
        Assert.False(BottomMostStrategy.Apply(new nint(10), LayerStrategy.BottomMost, api));
    }

    [Fact]
    public void RewriteWindowPosHonorsGuardsAndRewritesBottomMostAndTopMost()
    {
        var api = new FakeWindowLayerApi();
        WindowInterop.WINDOWPOS value = new() { hwnd = new nint(20), hwndInsertAfter = WindowInterop.HWND_TOPMOST };
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WindowInterop.WINDOWPOS>());
        try
        {
            Marshal.StructureToPtr(value, pointer, false);
            Assert.False(BottomMostStrategy.RewriteWindowPosForLayer(0, LayerStrategy.BottomMost, false, api));
            Assert.False(BottomMostStrategy.RewriteWindowPosForLayer(pointer, LayerStrategy.BottomMost, true, api));
            Assert.False(BottomMostStrategy.RewriteWindowPosForLayer(pointer, LayerStrategy.Normal, false, api));

            value.flags = WindowInterop.SWP_NOZORDER;
            Marshal.StructureToPtr(value, pointer, false);
            Assert.False(BottomMostStrategy.RewriteWindowPosForLayer(pointer, LayerStrategy.BottomMost, false, api));

            value.flags = 0;
            value.hwndInsertAfter = WindowInterop.HWND_TOPMOST;
            Marshal.StructureToPtr(value, pointer, false);
            Assert.True(BottomMostStrategy.RewriteWindowPosForLayer(pointer, LayerStrategy.BottomMost, false, api));
            value = Marshal.PtrToStructure<WindowInterop.WINDOWPOS>(pointer);
            Assert.Equal(WindowInterop.HWND_BOTTOM, value.hwndInsertAfter);
            Assert.False(BottomMostStrategy.RewriteWindowPosForLayer(pointer, LayerStrategy.BottomMost, false, api));

            value.hwndInsertAfter = WindowInterop.HWND_NOTOPMOST;
            Marshal.StructureToPtr(value, pointer, false);
            Assert.True(BottomMostStrategy.RewriteWindowPosForLayer(pointer, LayerStrategy.TopMost, false, api));
            value = Marshal.PtrToStructure<WindowInterop.WINDOWPOS>(pointer);
            Assert.Equal(WindowInterop.HWND_TOPMOST, value.hwndInsertAfter);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [Fact]
    public void RepairEngineSkipsInvalidHandleAndRecognizesHealthyTopMost()
    {
        var api = new FakeWindowLayerApi { Style = new(true, true, 0) };
        var engine = new WindowLayerRepairEngine(api);

        Assert.Equal(LayerRepairOutcome.Skipped, engine.Repair(0, LayerStrategy.TopMost, LayerRepairTrigger.Timer, true).Outcome);
        LayerRepairResult result = engine.Repair(new nint(10), LayerStrategy.TopMost, LayerRepairTrigger.Timer, true);
        Assert.Equal(LayerRepairOutcome.Healthy, result.Outcome);
        Assert.False(result.ApplyResult.SetWindowPosSucceeded);
        Assert.Equal(0, api.SetCalls);
    }

    [Fact]
    public void RepairEngineAppliesBottomMostAndResetsFailureEpisode()
    {
        var api = new FakeWindowLayerApi { Style = new(true, true, 0) };
        var engine = new WindowLayerRepairEngine(api);

        LayerRepairResult result = engine.Repair(new nint(10), LayerStrategy.BottomMost, LayerRepairTrigger.SourceInitialized, true);

        Assert.Equal(LayerRepairOutcome.Healthy, result.Outcome);
        Assert.True(result.ApplyResult.SetWindowPosSucceeded);
        Assert.Equal(0, engine.ConsecutiveFailures);
        Assert.Equal(WindowInterop.HWND_BOTTOM, api.LastInsertAfter);
    }

    [Fact]
    public void RepairEngineReturnsFallbackPendingAfterThreeCountedBottomMostFailures()
    {
        var api = new FakeWindowLayerApi { SetSucceeded = false };
        var engine = new WindowLayerRepairEngine(api);

        LayerRepairResult first = engine.Repair(new nint(10), LayerStrategy.BottomMost, LayerRepairTrigger.Timer, true);
        LayerRepairResult second = engine.Repair(new nint(10), LayerStrategy.BottomMost, LayerRepairTrigger.Timer, true);
        LayerRepairResult third = engine.Repair(new nint(10), LayerStrategy.BottomMost, LayerRepairTrigger.Timer, true);

        Assert.Equal(LayerRepairOutcome.Failed, first.Outcome);
        Assert.Equal(LayerRepairOutcome.Failed, second.Outcome);
        Assert.Equal(LayerRepairOutcome.FallbackPending, third.Outcome);
        Assert.Equal(3, third.ConsecutiveFailures);
        engine.ResetEpisode();
        Assert.Equal(0, engine.ConsecutiveFailures);
    }

    [Fact]
    public void RepairEngineReportsStyleReadMismatchAndManagedFailures()
    {
        var api = new FakeWindowLayerApi { Style = new(false, false, 5) };
        var engine = new WindowLayerRepairEngine(api);
        LayerRepairResult style = engine.Repair(new nint(10), LayerStrategy.TopMost, LayerRepairTrigger.Timer, true);
        Assert.Equal(LayerFailureKind.StyleReadFailed, style.ApplyResult.FailureKind);

        api.Style = new(true, false, 0);
        api.AfterStyle = new(true, false, 0);
        LayerRepairResult mismatch = engine.Repair(new nint(10), LayerStrategy.TopMost, LayerRepairTrigger.Timer, true, forceApply: true);
        Assert.Equal(LayerFailureKind.StyleMismatch, mismatch.ApplyResult.FailureKind);

        api.ThrowOnSet = true;
        LayerRepairResult exception = engine.Repair(new nint(10), LayerStrategy.BottomMost, LayerRepairTrigger.Timer, true);
        Assert.Equal(LayerFailureKind.ManagedException, exception.ApplyResult.FailureKind);
        Assert.Equal(1, exception.ConsecutiveFailures);
    }

    [Fact]
    public void NativeWindowLayerApiUsesInjectedDelegatesAndReportsErrors()
    {
        var adapter = new NativeWindowLayerApi(
            (hWnd, _) => new nint(WindowInterop.WS_EX_TOPMOST),
            (_, _, _, _, _, _, _) => true);
        WindowStyleObservation style = adapter.GetExtendedStyle(new nint(10));
        Assert.True(style.Succeeded);
        Assert.True(style.IsTopMost);
        Assert.True(adapter.SetWindowPosition(new nint(10), WindowInterop.HWND_BOTTOM, 0).Succeeded);

        var failed = new NativeWindowLayerApi(
            (_, _) =>
            {
                Marshal.SetLastPInvokeError(1400);
                return 0;
            },
            (_, _, _, _, _, _, _) =>
            {
                Marshal.SetLastPInvokeError(5);
                return false;
            });
        Assert.False(failed.GetExtendedStyle(new nint(10)).Succeeded);
        WindowPositionCallResult position = failed.SetWindowPosition(new nint(10), WindowInterop.HWND_BOTTOM, 0);
        Assert.False(position.Succeeded);
        Assert.Equal(5, position.ErrorCode);
    }

    private sealed class FakeWindowLayerApi : IWindowLayerApi
    {
        public WindowStyleObservation Style { get; set; } = new(true, false, 0);
        public WindowStyleObservation AfterStyle { get; set; } = new(true, false, 0);
        public bool SetSucceeded { get; set; } = true;
        public bool ThrowOnSet { get; set; }
        public int SetCalls { get; private set; }
        public nint LastInsertAfter { get; private set; }
        public uint LastFlags { get; private set; }

        public WindowStyleObservation GetExtendedStyle(nint hWnd) => SetCalls == 0 ? Style : AfterStyle;

        public WindowPositionCallResult SetWindowPosition(nint hWnd, nint insertAfter, uint flags)
        {
            SetCalls++;
            LastInsertAfter = insertAfter;
            LastFlags = flags;
            if (ThrowOnSet) throw new InvalidOperationException("test");
            return new(SetSucceeded, SetSucceeded ? 0 : 5);
        }
    }
}
