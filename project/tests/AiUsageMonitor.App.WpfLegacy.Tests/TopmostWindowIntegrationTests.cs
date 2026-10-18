using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using AiUsageMonitor.Core.Settings;
using Xunit.Abstractions;

namespace AiUsageMonitor.App.Tests;

[Trait("Category", "Interactive")]
public sealed class TopmostWindowIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public void ExternalTopmostActivationRecoversWidgetWithoutChangingForeground()
    {
        if (!Environment.UserInteractive)
        {
            output.WriteLine("SKIPPED: Interactive desktop session is unavailable.");
            return;
        }

        string hostPath = AiUsageMonitor.TestSupport.FakeExecutableLocator.FindTopmostTestHost();
        using Process host = StartHost(hostPath);
        bool foregroundUnavailable = false;
        try
        {
            MainWindowScaleTestSupport.RunInSta(() =>
            {
                var window = new MainWindow
                {
                    DataContext = MainWindowScaleTestSupport.CreateMaxDisplayViewModel(),
                };
                try
                {
                    window.ApplySettings(new AppSettings
                    {
                        AlwaysOnTop = true,
                        ClickThrough = true,
                    }, reposition: false);
                    window.Show();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    nint widgetHandle = new WindowInteropHelper(window).Handle;
                    nint hostHandle = WaitForMainWindow(host);
                    Assert.NotEqual(0, widgetHandle);
                    Assert.NotEqual(0, hostHandle);

                    ShowWindow(hostHandle, 9);
                    SetForegroundWindow(hostHandle);
                    if (!TryWaitFor(() => GetForegroundWindow() == hostHandle))
                    {
                        foregroundUnavailable = true;
                        return;
                    }
                    bool above = TryWaitFor(() => IsAbove(widgetHandle, hostHandle));
                    output.WriteLine(
                        $"widget={widgetHandle}, host={hostHandle}, above={above}, " +
                        $"widgetIndex={ZOrderIndex(widgetHandle)}, hostIndex={ZOrderIndex(hostHandle)}, " +
                        $"widgetExStyle=0x{GetWindowLongPtr(widgetHandle, -20).ToInt64():X}, " +
                        $"hostExStyle=0x{GetWindowLongPtr(hostHandle, -20).ToInt64():X}");
                    Assert.True(above);
                    Assert.Equal(hostHandle, GetForegroundWindow());

                    window.ApplySettings(new AppSettings { AlwaysOnTop = false }, reposition: false);
                    WaitFor(() => !IsAbove(widgetHandle, hostHandle));
                    Assert.Equal(hostHandle, GetForegroundWindow());

                    window.ApplySettings(new AppSettings { AlwaysOnTop = true }, reposition: false);
                    window.Hide();
                    window.Show();
                    WaitFor(() => IsAbove(widgetHandle, hostHandle));
                    Assert.Equal(hostHandle, GetForegroundWindow());
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            if (!host.HasExited)
                host.Kill(entireProcessTree: true);
            host.WaitForExit(5000);
        }
        if (foregroundUnavailable)
            output.WriteLine("SKIPPED: The interactive session rejected foreground transfer to the test host.");
    }

    private static Process StartHost(string path)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = path,
            Arguments = "--topmost",
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("Failed to start TopMost test host.");
        return process;
    }

    private static nint WaitForMainWindow(Process process)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            process.Refresh();
            if (process.HasExited) break;
            if (process.MainWindowHandle != 0) return process.MainWindowHandle;
            Thread.Sleep(50);
        }
        throw Xunit.Sdk.SkipException.ForSkip("TopMost test host did not expose an interactive window.");
    }

    private static void WaitFor(Func<bool> condition)
    {
        Assert.True(TryWaitFor(condition), "Timed out waiting for the expected window state.");
    }

    private static bool TryWaitFor(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        return condition();
    }

    private static bool IsAbove(nint candidate, nint other)
    {
        for (nint current = GetTopWindow(0); current != 0; current = GetWindow(current, GetWindowNext))
        {
            if (current == candidate) return true;
            if (current == other) return false;
        }
        return false;
    }

    private static int ZOrderIndex(nint target)
    {
        int index = 0;
        for (nint current = GetTopWindow(0); current != 0; current = GetWindow(current, GetWindowNext))
        {
            if (current == target) return index;
            index++;
        }
        return -1;
    }

    private const uint GetWindowNext = 2;

    [DllImport("user32.dll")]
    private static extern nint GetTopWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint windowHandle, uint command);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);
}
