using System.Drawing;
using System.IO;
using Forms = System.Windows.Forms;

namespace AiUsageMonitor.App;

public sealed class TrayController : IDisposable
{
    private readonly Icon _trayIcon;
    private readonly Forms.NotifyIcon _icon;
    public event Action? ClaudeSetupRequested;
    public event Action? SettingsRequested;
    public event Action? RefreshRequested;
    public event Action? ToggleVisibilityRequested;
    public event Action? ExitRequested;

    public TrayController()
    {
        System.Windows.Resources.StreamResourceInfo resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/ai-usage-monitor.ico"));
        using (Stream stream = resource.Stream)
        using (var sourceIcon = new Icon(stream))
        {
            _trayIcon = (Icon)sourceIcon.Clone();
        }
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Claude Code連携…", null, (_, _) => ClaudeSetupRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("設定…", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add("今すぐ更新", null, (_, _) => RefreshRequested?.Invoke());
        menu.Items.Add("表示／非表示", null, (_, _) => ToggleVisibilityRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitRequested?.Invoke());
        _icon = new Forms.NotifyIcon { Visible = true, Text = "AI Usage Monitor", Icon = _trayIcon, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => SettingsRequested?.Invoke();
    }
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); _trayIcon.Dispose(); }
}
