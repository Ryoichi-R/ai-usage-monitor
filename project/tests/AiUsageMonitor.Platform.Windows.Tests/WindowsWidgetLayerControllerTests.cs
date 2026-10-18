using AiUsageMonitor.Platform.Windows.Window;

namespace AiUsageMonitor.Platform.Windows.Tests;

public sealed class WindowsWidgetLayerControllerTests
{
    [Theory]
    [InlineData(WidgetLayerMode.Normal)]
    [InlineData(WidgetLayerMode.AlwaysOnBottom)]
    public void NonTopmostFailureAndRecoveryPropagateHealth(WidgetLayerMode mode)
    {
        using var controller = CreateController(out _, out FakeWindowLayerApi api);
        var received = new List<WidgetLayerHealth>();
        controller.HealthChanged += received.Add;
        controller.Attach(new nint(10));
        api.Result = new(false, 5);
        controller.SetLayerMode(mode);
        Assert.True(controller.Health.IsDegraded);
        Assert.Equal(5, controller.Health.ErrorCode);
        Assert.False(controller.TryRecoverLayer());
        api.Result = new(true, 0);
        Assert.True(controller.TryRecoverLayer());
        Assert.False(controller.Health.IsDegraded);
        Assert.Equal(2, received.Count);
        Assert.True(received[0].IsDegraded);
        Assert.False(received[1].IsDegraded);
    }

    [Fact]
    public void SwitchingFromFailedBottomLayerToTopmostClearsHealth()
    {
        using var controller = CreateController(out _, out FakeWindowLayerApi api);
        controller.Attach(new nint(10));
        api.Result = new(false, 5);
        controller.SetLayerMode(WidgetLayerMode.AlwaysOnBottom);
        Assert.True(controller.Health.IsDegraded);
        controller.SetLayerMode(WidgetLayerMode.AlwaysOnTop);
        Assert.False(controller.Health.IsDegraded);
    }

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
    public void AlwaysOnTopImmediatelyAppliesTopmostWithoutActivation()
    {
        using var controller = CreateController(out FakeTopmostInterop topmost, out FakeWindowLayerApi bottomMostApi);
        controller.Attach(new nint(10));

        controller.SetLayerMode(WidgetLayerMode.AlwaysOnTop);

        Assert.Equal(3, topmost.Hooks.Count);
        Assert.Equal(1, topmost.SetCalls);
        Assert.Equal(new nint(10), topmost.LastWindowPosition.WindowHandle);
        Assert.Equal(new nint(-1), topmost.LastWindowPosition.InsertAfter);
        Assert.Equal(0x0013u, topmost.LastWindowPosition.Flags);
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
    public void SetClickThroughRequiresAttachAndReportsInvalidNativeHandle()
    {
        using var controller = CreateController(out _, out _);
        Assert.Throws<InvalidOperationException>(() => controller.SetClickThrough(true));

        controller.Attach(new nint(10));
        Assert.Throws<System.ComponentModel.Win32Exception>(() => controller.SetClickThrough(true));
    }

    [Fact]
    public void DisposedControllerThrowsOnFurtherUse()
    {
        var controller = CreateController(out _, out _);
        controller.Attach(new nint(10));
        controller.Dispose();

        Assert.Throws<ObjectDisposedException>(() => controller.SetLayerMode(WidgetLayerMode.AlwaysOnTop));
    }

    [Theory]
    [InlineData(0x0003u)]
    [InlineData(0x000Au)]
    [InlineData(0x0020u)]
    public void ExternalWindowEventsReapplyTopmost(uint eventType)
    {
        using var controller = CreateController(out FakeTopmostInterop topmost, out _);
        controller.Attach(new nint(10));
        controller.SetLayerMode(WidgetLayerMode.AlwaysOnTop);

        topmost.Raise(eventType);

        Assert.Equal(2, topmost.SetCalls);
        Assert.Equal(new nint(-1), topmost.LastWindowPosition.InsertAfter);
        Assert.Equal(0x0013u, topmost.LastWindowPosition.Flags);
    }

    [Theory]
    [InlineData(WidgetLayerMode.Normal)]
    [InlineData(WidgetLayerMode.AlwaysOnBottom)]
    public void LeavingTopmostStopsRecoveryFromLateCallbacks(WidgetLayerMode mode)
    {
        using var controller = CreateController(out FakeTopmostInterop topmost, out FakeWindowLayerApi bottomMostApi);
        controller.Attach(new nint(10));
        controller.SetLayerMode(WidgetLayerMode.AlwaysOnTop);
        controller.SetLayerMode(mode);

        topmost.Raise(0x0003);

        Assert.Equal(1, topmost.SetCalls);
        Assert.Equal(3, topmost.Unhooked.Count);
        Assert.Equal(1, bottomMostApi.SetCalls);
        Assert.Equal(mode == WidgetLayerMode.Normal ? new nint(-2) : new nint(1), bottomMostApi.LastInsertAfter);
    }

    [Fact]
    public void RecoveryFailureIsRetriedOnTheNextExternalEvent()
    {
        using var controller = CreateController(out FakeTopmostInterop topmost, out _);
        controller.Attach(new nint(10));
        topmost.ReturnWindowPositionResult = false;
        topmost.LastErrorCode = 5;
        controller.SetLayerMode(WidgetLayerMode.AlwaysOnTop);
        Assert.True(controller.Health.IsDegraded);

        topmost.ReturnWindowPositionResult = true;
        topmost.Raise(0x0003);

        Assert.Equal(2, topmost.SetCalls);
        Assert.False(controller.Health.IsDegraded);
    }

    [Fact]
    public void NestedNativeCallbackDoesNotRecursivelyReapplyTopmost()
    {
        using var controller = CreateController(out FakeTopmostInterop topmost, out _);
        controller.Attach(new nint(10));
        topmost.OnSetWindowPos = () => topmost.Raise(0x0003);

        controller.SetLayerMode(WidgetLayerMode.AlwaysOnTop);
        topmost.Raise(0x000A);

        Assert.Equal(2, topmost.SetCalls);
    }

    [Fact]
    public void DisposingStopsRecoveryFromLateCallbacks()
    {
        var controller = CreateController(out FakeTopmostInterop topmost, out _);
        controller.Attach(new nint(10));
        controller.SetLayerMode(WidgetLayerMode.AlwaysOnTop);
        controller.Dispose();

        topmost.Raise(0x0003);

        Assert.Equal(1, topmost.SetCalls);
        Assert.Equal(3, topmost.Unhooked.Count);
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
        public int SetCalls { get; private set; }
        public Action? OnSetWindowPos { get; set; }
        private int _nextHook = 1;

        public void Raise(uint eventType)
        {
            var hook = Hooks.Single(hook => hook.Min == eventType);
            hook.Callback(new nint(1), eventType, new nint(99), 0, 0, 0, 0);
        }

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
            SetCalls++;
            LastWindowPosition = (windowHandle, insertAfter, flags);
            OnSetWindowPos?.Invoke();
            return ReturnWindowPositionResult;
        }
    }

    private sealed class FakeWindowLayerApi : IWindowLayerApi
    {
        public WindowPositionCallResult Result { get; set; } = new(true, 0);
        public int SetCalls { get; private set; }
        public nint LastInsertAfter { get; private set; }

        public WindowStyleObservation GetExtendedStyle(nint hWnd) => new(true, false, 0);

        public WindowPositionCallResult SetWindowPosition(nint hWnd, nint insertAfter, uint flags)
        {
            SetCalls++;
            LastInsertAfter = insertAfter;
            return Result;
        }
    }
}
