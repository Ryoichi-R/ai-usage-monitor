using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AiUsageMonitor.Core.Settings;
using Xunit.Abstractions;

namespace AiUsageMonitor.App.Tests;

[Trait("Category", "Interactive")]
public sealed class TopmostFlagProbeTests(ITestOutputHelper output)
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly nint HwndTopmost = new(-1);

    [Fact]
    public void OwnedModalProbeRecordsTheAdoptedNoOwnerZOrderContract()
    {
        if (!Environment.UserInteractive)
            throw Xunit.Sdk.SkipException.ForSkip("Interactive desktop session is unavailable.");

        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var owner = new MainWindow();
            try
            {
                owner.ApplySettings(new AppSettings { AlwaysOnTop = true, ClickThrough = false }, false);
                owner.Show();
                owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                nint ownerHandle = new WindowInteropHelper(owner).Handle;

                ProbeDialog(owner, ownerHandle, SwpNoOwnerZOrder, "with-noowner");
                ProbeDialog(owner, ownerHandle, 0, "without-noowner");
            }
            finally
            {
                owner.Close();
            }
        });
    }

    private void ProbeDialog(MainWindow owner, nint ownerHandle, uint ownerFlags, string label)
    {
        var dialog = new Window
        {
            Owner = owner,
            Width = 180,
            Height = 100,
            Topmost = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = label,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Exception? callbackException = null;
        bool scheduled = false;
        dialog.ContentRendered += (_, _) =>
        {
            if (scheduled) return;
            scheduled = true;
            dialog.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    nint dialogHandle = new WindowInteropHelper(dialog).Handle;
                    dialog.Activate();
                    SetActiveWindow(dialogHandle);
                    SetFocus(dialogHandle);
                    SetForegroundWindow(dialogHandle);
                    bool setResult = SetWindowPos(
                        dialogHandle,
                        HwndTopmost,
                        0,
                        0,
                        0,
                        0,
                        SwpNoMove | SwpNoSize | SwpNoActivate | ownerFlags);
                    nint foreground = GetForegroundWindow();
                    bool dialogAboveOwner = IsAbove(dialogHandle, ownerHandle);
                    output.WriteLine(
                        $"{label}: SetWindowPos={setResult}, flags=0x{(SwpNoMove | SwpNoSize | SwpNoActivate | ownerFlags):X}, " +
                        $"owner={ownerHandle}, dialog={dialogHandle}, foreground={foreground}, " +
                        $"dialogAboveOwner={dialogAboveOwner}, foregroundIsDialog={foreground == dialogHandle}");
                    Assert.True(setResult);
                    Assert.True(dialogAboveOwner);
                }
                catch (Exception exception)
                {
                    callbackException = exception;
                }
                finally
                {
                    dialog.Close();
                }
            }));
        };

        dialog.ShowDialog();
        if (callbackException is not null) throw callbackException;
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

    private const uint GetWindowNext = 2;

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetTopWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint windowHandle, uint command);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint windowHandle);
}
