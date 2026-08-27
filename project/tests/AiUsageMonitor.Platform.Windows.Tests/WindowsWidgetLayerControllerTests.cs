using AiUsageMonitor.Platform.Windows.Window;

namespace AiUsageMonitor.Platform.Windows.Tests;

public sealed class WindowsWidgetLayerControllerTests
{
    [Fact]
    public void SetLayerModeBeforeAttachThrows()
    {
        using var controller = CreateController(out _, out _);

        Assert.Throws<InvalidOperationException>(() => controller.SetLayerMode(WidgetLayerMode.AlwaysOnTop));
    }

    [Fact]
    public void AttachingTwiceThrows()
    {
        using var controller = CreateController(out _, out _);
        controller.Attach(new nint(10));

        Assert.Throws<InvalidOperationException>(() => controller.Attach(new nint(20)));
    }

    [Fact]
    public void AlwaysOnTopEnablesTopmostAndDoesNotTouchBottomMost()
    {
        using var controller = CreateController(out FakeTopmostInterop topmost, out FakeWindowLayerApi bottomMostApi);
        controller.Attach(new nint(10));

        controller.SetLayerMode(WidgetLayerMode.AlwaysOnTop);

        Assert.Equal(3, topmost.Hooks.Count);
        Assert.Equal(0, bottomMostApi.SetCalls);
    }

    [Fact]
    public void AlwaysOnBottomAppliesBottomMostThroughTheInjectedApi()
    {
        using var controller = CreateController(out _, out FakeWindowLayerApi bottomMostApi);
        controller.Attach(new nint(10));

        controller.SetLayerMode(WidgetLayerMode.AlwaysOnBottom);

        Assert.Equal(1, bottomMostApi.SetCalls);
        Assert.Equal(NativeWindowPositionerConstants.HwndBottom, bottomMostApi.LastInsertAfter);
    }

    [Fact]
    public void TryRecoverLayerDelegatesToTopmostWhenModeIsAlwaysOnTop()
    {
        using var controller = CreateController(out FakeTopmostInterop topmost, out _);
        controller.Attach(new nint(10));
        controller.SetLayerMode(WidgetLayerMode.AlwaysOnTop);

        bool recovered = controller.TryRecoverLayer();

        Assert.True(recovered);
        Assert.Equal(new nint(10), topmost.LastWindowPosition.WindowHandle);
    }

    [Fact]
    public void TryRecoverLayerReturnsFalseBeforeAttach()
    {
        using var controller = CreateController(out _, out _);

        Assert.False(controller.TryRecoverLayer());
    }

    [Fact]
    public void HealthChangedPropagatesFromTheUnderlyingTopmostController()
    {
        using var controller = CreateController(out FakeTopmostInterop topmost, out _);
        var received = new List<WidgetLayerHealth>();
        controller.HealthChanged += received.Add;
        controller.Attach(new nint(10));
        controller.SetLayerMode(WidgetLayerMode.AlwaysOnTop);
        topmost.ReturnWindowPositionResult = false;
        topmost.LastErrorCode = 1400;

        controller.TryRecoverLayer();

        Assert.Contains(received, health => health.IsDegraded && health.ErrorCode == 1400);
        Assert.True(controller.Health.IsDegraded);
    }

    [Fact]
    public void SetClickThroughRequiresAttachAndOtherwiseCompletesWithoutThrowing()
    {
        using var controller = CreateController(out _, out _);
        Assert.Throws<InvalidOperationException>(() => controller.SetClickThrough(true));

        controller.Attach(new nint(10));
        controller.SetClickThrough(true);
        controller.SetClickThrough(false);
    }

    [Fact]
    public void DisposedControllerThrowsOnFurtherUse()
    {
        var controller = CreateController(out _, out _);
        controller.Attach(new nint(10));
        controller.Dispose();

        Assert.Throws<ObjectDisposedException>(() => controller.SetLayerMode(WidgetLayerMode.AlwaysOnTop));
    }

    private static WindowsWidgetLayerController CreateController(out FakeTopmostInterop topmost, out FakeWindowLayerApi bottomMostApi)
    {
        var capturedTopmost = new FakeTopmostInterop();
        var capturedApi = new FakeWindowLayerApi();
        topmost = capturedTopmost;
        bottomMostApi = capturedApi;
        return new WindowsWidgetLayerController(handle => new TopmostWindowController(handle, capturedTopmost), capturedApi);
    }

    private static class NativeWindowPositionerConstants
    {
        public static readonly nint HwndBottom = WindowInterop.HWND_BOTTOM;
    }

    private sealed class FakeTopmostInterop : ITopmostInterop
    {
        public List<(uint Min, uint Max, uint Flags, WinEventCallback Callback)> Hooks { get; } = [];
        public List<nint> Unhooked { get; } = [];
        public (nint WindowHandle, nint InsertAfter, uint Flags) LastWindowPosition { get; private set; }
        public bool ReturnWindowPositionResult { get; set; } = true;
        public int LastErrorCode { get; set; }
        private int _nextHook = 1;

        public nint SetWinEventHook(uint eventMin, uint eventMax, uint flags, WinEventCallback callback)
        {
            Hooks.Add((eventMin, eventMax, flags, callback));
            return new nint(_nextHook++);
        }

        public bool UnhookWinEvent(nint hookHandle)
        {
            Unhooked.Add(hookHandle);
            return true;
        }

        public bool SetWindowPos(nint windowHandle, nint insertAfter, uint flags)
        {
            LastWindowPosition = (windowHandle, insertAfter, flags);
            return ReturnWindowPositionResult;
        }
    }

    private sealed class FakeWindowLayerApi : IWindowLayerApi
    {
        public int SetCalls { get; private set; }
        public nint LastInsertAfter { get; private set; }

        public WindowStyleObservation GetExtendedStyle(nint hWnd) => new(true, false, 0);

        public WindowPositionCallResult SetWindowPosition(nint hWnd, nint insertAfter, uint flags)
        {
            SetCalls++;
            LastInsertAfter = insertAfter;
            return new(true, 0);
        }
    }
}
