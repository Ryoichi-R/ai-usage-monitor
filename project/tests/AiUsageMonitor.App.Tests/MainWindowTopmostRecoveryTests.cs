using System.Windows.Interop;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Platform.Windows.Window;

namespace AiUsageMonitor.App.Tests;

public sealed class MainWindowTopmostRecoveryTests
{
    [Theory]
    [InlineData(false, true, true, true, true)]
    [InlineData(true, true, true, true, false)]
    [InlineData(false, false, true, true, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(false, true, true, false, false)]
    public void CanRecoverTopmostRequiresAllFourGuards(
        bool isClosing,
        bool hasHandle,
        bool isVisible,
        bool alwaysOnTop,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.CanRecoverTopmost(isClosing, hasHandle, isVisible, alwaysOnTop));
    }

    [Fact]
    public void HiddenWindowWithoutTestOverrideDoesNotReserveOrCallNative()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = new MainWindow();
            var interop = new FakeTopmostInterop();
            var controller = CreateController(interop);
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                window.ApplySettings(new AppSettings { AlwaysOnTop = true }, reposition: false);
                window.SetTopmostControllerForTest(controller);

                window.RequestTopmostRecoveryForTest();

                Assert.False(window.HasPendingTopmostForTest);
                Assert.Equal(0, interop.WindowPositionCalls);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void HiddenWindowOverrideCoalescesAndDrainsOneRecovery()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = new MainWindow();
            var interop = new FakeTopmostInterop();
            var controller = CreateController(interop);
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                window.ApplySettings(new AppSettings { AlwaysOnTop = true }, reposition: false);
                window.SetTopmostControllerForTest(controller);
                window.TopmostRecoveryVisibilityOverrideForTest = true;

                window.RequestTopmostRecoveryForTest();
                window.RequestTopmostRecoveryForTest();
                Assert.True(window.HasPendingTopmostForTest);

                window.DrainPendingTopmostForTest();

                Assert.False(window.HasPendingTopmostForTest);
                Assert.Equal(1, interop.WindowPositionCalls);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ReentrantRequestDoesNotCreateAnAdditionalRecovery()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = new MainWindow();
            var interop = new FakeTopmostInterop();
            var controller = CreateController(interop);
            interop.OnSetWindowPos = () => window.RequestTopmostRecoveryForTest();
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                window.ApplySettings(new AppSettings { AlwaysOnTop = true }, reposition: false);
                window.SetTopmostControllerForTest(controller);
                window.TopmostRecoveryVisibilityOverrideForTest = true;
                window.RequestTopmostRecoveryForTest();

                window.DrainPendingTopmostForTest();

                Assert.False(window.HasPendingTopmostForTest);
                Assert.Equal(1, interop.WindowPositionCalls);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TurningTopmostOffCancelsPendingRecoveryAndDisablesController()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = new MainWindow();
            var interop = new FakeTopmostInterop();
            var controller = CreateController(interop);
            try
            {
                new WindowInteropHelper(window).EnsureHandle();
                window.ApplySettings(new AppSettings { AlwaysOnTop = true }, reposition: false);
                window.SetTopmostControllerForTest(controller);
                window.TopmostRecoveryVisibilityOverrideForTest = true;
                window.RequestTopmostRecoveryForTest();
                Assert.True(window.HasPendingTopmostForTest);

                window.ApplySettings(new AppSettings { AlwaysOnTop = false }, reposition: false);
                window.DrainPendingTopmostForTest();

                Assert.False(window.HasPendingTopmostForTest);
                Assert.Equal(0, interop.WindowPositionCalls);
                Assert.False(window.Topmost);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static TopmostWindowController CreateController(FakeTopmostInterop interop)
    {
        var controller = new TopmostWindowController(new nint(123), interop);
        controller.SetEnabled(true);
        return controller;
    }

    private sealed class FakeTopmostInterop : ITopmostInterop
    {
        private readonly List<WinEventCallback> _callbacks = [];
        private int _nextHook = 1;

        public int WindowPositionCalls { get; private set; }
        public Action? OnSetWindowPos { get; set; }
        public int LastErrorCode { get; set; }

        public nint SetWinEventHook(uint eventMin, uint eventMax, uint flags, WinEventCallback callback)
        {
            _callbacks.Add(callback);
            return new nint(_nextHook++);
        }

        public bool UnhookWinEvent(nint hookHandle) => true;

        public bool SetWindowPos(nint windowHandle, nint insertAfter, uint flags)
        {
            WindowPositionCalls++;
            OnSetWindowPos?.Invoke();
            return true;
        }
    }
}
