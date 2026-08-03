using AiUsageMonitor.Windows.Window;

namespace AiUsageMonitor.Windows.Tests;

public sealed class TopmostWindowControllerTests
{
    [Fact]
    public void EnableRegistersTheThreeExactWinEventHooksAndEventRequestsRecovery()
    {
        var interop = new FakeTopmostInterop();
        using var controller = new TopmostWindowController(new nint(123), interop);
        int requests = 0;
        controller.RecoveryRequested += () => requests++;

        controller.SetEnabled(true);
        interop.RaiseAll();

        Assert.Equal(3, interop.Hooks.Count);
        Assert.All(interop.Hooks, hook => Assert.Equal(TopmostWindowController.HookFlags, hook.Flags));
        Assert.Equal(3, requests);
        Assert.Equal(controller.RegistrationThreadId, Environment.CurrentManagedThreadId);
    }

    [Fact]
    public void DisableUnhooksAndFurtherCallbacksAreIgnored()
    {
        var interop = new FakeTopmostInterop();
        using var controller = new TopmostWindowController(new nint(123), interop);
        int requests = 0;
        controller.RecoveryRequested += () => requests++;
        controller.SetEnabled(true);

        controller.SetEnabled(false);
        interop.RaiseAll();

        Assert.Equal(3, interop.Unhooked.Count);
        Assert.Equal(0, requests);
    }

    [Fact]
    public void RecoveryUsesNoMoveNoSizeNoActivateAndNoOwnerZOrder()
    {
        var interop = new FakeTopmostInterop();
        using var controller = new TopmostWindowController(new nint(123), interop);
        controller.SetEnabled(true);

        Assert.True(controller.TryRecover());
        Assert.Equal(TopmostWindowController.RecoveryFlags, interop.LastWindowPosition.Flags);
        Assert.Equal(new nint(123), interop.LastWindowPosition.WindowHandle);
    }

    [Fact]
    public void NativeFailureReturnsFalseWithoutThrowingAndCanBeRetried()
    {
        var interop = new FakeTopmostInterop { ReturnWindowPositionResult = false, LastErrorCode = 1400 };
        using var controller = new TopmostWindowController(new nint(123), interop);
        controller.SetEnabled(true);

        Assert.False(controller.TryRecover());
        Assert.True(controller.Health.IsDegraded);
        Assert.Equal("SetWindowPos", controller.Health.Operation);
        Assert.Equal(1400, controller.Health.ErrorCode);
        Assert.Equal(1, controller.Health.ConsecutiveFailures);
        interop.ReturnWindowPositionResult = true;
        Assert.True(controller.TryRecover());
        Assert.False(controller.Health.IsDegraded);
        Assert.Equal(2, interop.WindowPositionCalls);
    }

    [Fact]
    public void PartialRegistrationIsCleanedUpAndDisposeIsIdempotent()
    {
        var interop = new FakeTopmostInterop { FailRegistrationNumber = 2 };
        var controller = new TopmostWindowController(new nint(123), interop);

        controller.SetEnabled(true);
        controller.Dispose();
        controller.Dispose();

        Assert.Single(interop.Unhooked);
        Assert.Equal(1, interop.Unhooked[0]);
    }

    [Fact]
    public void RegistrationFailureRecordsHealthAndRetriesOnNextEnable()
    {
        var interop = new FakeTopmostInterop
        {
            FailRegistrationNumber = 1,
            LastErrorCode = 5,
        };
        using var controller = new TopmostWindowController(new nint(123), interop);

        controller.SetEnabled(true);

        Assert.True(controller.Health.IsDegraded);
        Assert.Equal("SetWinEventHook", controller.Health.Operation);
        Assert.Equal(5, controller.Health.ErrorCode);
        Assert.Equal(1, controller.Health.ConsecutiveFailures);
        Assert.Empty(controller.HookHandles);

        interop.FailRegistrationNumber = null;
        controller.SetEnabled(true);

        Assert.False(controller.Health.IsDegraded);
        Assert.Equal(3, controller.HookHandles.Count);
    }

    [Fact]
    public void RegistrationFailurePreservesErrorCodeAcrossRollback()
    {
        var interop = new FakeTopmostInterop
        {
            FailRegistrationNumber = 2,
            LastErrorCode = 5,
            ClearLastErrorOnUnhook = true,
        };
        using var controller = new TopmostWindowController(new nint(123), interop);

        controller.SetEnabled(true);

        Assert.True(controller.Health.IsDegraded);
        Assert.Equal(5, controller.Health.ErrorCode);
        Assert.Single(interop.Unhooked);
    }

    [Fact]
    public async Task DisposeFromWrongThreadThrowsSpecificException()
    {
        var interop = new FakeTopmostInterop();
        var ready = new TaskCompletionSource<TopmostWindowController>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeOnOwner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task owner = Task.Run(() =>
        {
            var ownedController = new TopmostWindowController(new nint(123), interop);
            ownedController.SetEnabled(true);
            ready.SetResult(ownedController);
            disposeOnOwner.Task.GetAwaiter().GetResult();
            ownedController.Dispose();
        });
        TopmostWindowController controller = await ready.Task;

        Exception? exception = Record.Exception(controller.Dispose);

        Assert.IsType<InvalidOperationException>(exception);
        disposeOnOwner.SetResult();
        await owner;
    }

    private sealed class FakeTopmostInterop : ITopmostInterop
    {
        private int _registrationCount;
        private int _nextHook = 1;
        private readonly List<WinEventCallback> _callbacks = [];

        public List<(uint EventMin, uint EventMax, uint Flags)> Hooks { get; } = [];
        public List<nint> Unhooked { get; } = [];
        public int? FailRegistrationNumber { get; set; }
        public int LastErrorCode { get; set; }
        public bool ClearLastErrorOnUnhook { get; init; }
        public bool ReturnWindowPositionResult { get; set; } = true;
        public int WindowPositionCalls { get; private set; }
        public (nint WindowHandle, nint InsertAfter, uint Flags) LastWindowPosition { get; private set; }

        public nint SetWinEventHook(uint eventMin, uint eventMax, uint flags, WinEventCallback callback)
        {
            _registrationCount++;
            if (_registrationCount == FailRegistrationNumber) return 0;
            Hooks.Add((eventMin, eventMax, flags));
            _callbacks.Add(callback);
            return new nint(_nextHook++);
        }

        public bool UnhookWinEvent(nint hookHandle)
        {
            Unhooked.Add(hookHandle);
            if (ClearLastErrorOnUnhook) LastErrorCode = 0;
            return true;
        }

        public bool SetWindowPos(nint windowHandle, nint insertAfter, uint flags)
        {
            WindowPositionCalls++;
            LastWindowPosition = (windowHandle, insertAfter, flags);
            return ReturnWindowPositionResult;
        }

        public void RaiseAll()
        {
            foreach (WinEventCallback callback in _callbacks.ToArray())
                callback(1, 0, new nint(456), 0, 0, 0, 0);
        }
    }
}
