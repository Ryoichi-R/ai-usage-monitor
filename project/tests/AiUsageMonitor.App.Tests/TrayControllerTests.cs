using System.Reflection;
using System.Runtime.ExceptionServices;
using AiUsageMonitor.Core.Settings;
using Forms = System.Windows.Forms;

namespace AiUsageMonitor.App.Tests;

public sealed class TrayControllerTests
{
    [Fact]
    public void MenuCommandsRaiseOnlyTheirMatchingEventsAndModeTracksSelection()
    {
        RunInSta(() =>
        {
            using var tray = new TrayController((System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone());
            var icon = GetIcon(tray);
            var menu = Assert.IsType<Forms.ContextMenuStrip>(icon.ContextMenuStrip);
            int setup = 0, settings = 0, refresh = 0, visibility = 0, exit = 0;
            var modes = new List<WidgetDisplayMode>();
            tray.ClaudeSetupRequested += () => setup++;
            tray.SettingsRequested += () => settings++;
            tray.RefreshRequested += () => refresh++;
            tray.ToggleVisibilityRequested += () => visibility++;
            tray.ExitRequested += () => exit++;
            tray.DisplayModeRequested += modes.Add;

            menu.Items[0].PerformClick();
            menu.Items[2].PerformClick();
            menu.Items[3].PerformClick();
            menu.Items[4].PerformClick();
            var display = Assert.IsType<Forms.ToolStripMenuItem>(menu.Items[5]);
            display.DropDownItems[1].PerformClick();
            display.DropDownItems[0].PerformClick();
            menu.Items[7].PerformClick();
            Assert.Equal((1, 1, 1, 1, 1), (setup, settings, refresh, visibility, exit));
            Assert.Equal([WidgetDisplayMode.Compact, WidgetDisplayMode.Standard], modes);

            tray.SetDisplayMode(WidgetDisplayMode.Compact);
            Assert.True(Assert.IsType<Forms.ToolStripMenuItem>(display.DropDownItems[1]).Checked);
            Assert.False(Assert.IsType<Forms.ToolStripMenuItem>(display.DropDownItems[0]).Checked);
            tray.SetDisplayMode(WidgetDisplayMode.Standard);
            Assert.True(Assert.IsType<Forms.ToolStripMenuItem>(display.DropDownItems[0]).Checked);
            Assert.False(Assert.IsType<Forms.ToolStripMenuItem>(display.DropDownItems[1]).Checked);
            tray.SetTopmostDegraded(true);
            Assert.Contains("TopMost復旧無効", icon.Text);
            tray.SetTopmostDegraded(false);
            Assert.Equal("AI Usage Monitor", icon.Text);
        });
    }

    private static Forms.NotifyIcon GetIcon(TrayController tray) =>
        Assert.IsType<Forms.NotifyIcon>(typeof(TrayController)
            .GetField("_icon", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(tray));

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}