using System.Drawing;
using System.IO;
using Forms = System.Windows.Forms;
using AiUsageMonitor.Core.Settings;

namespace AiUsageMonitor.App;

public sealed class TrayController : IDisposable
{
    private const string NormalTooltip = "AI Usage Monitor";
    private const string TopmostDegradedTooltip = "AI Usage Monitor（TopMost復旧無効）";
    private readonly Icon _trayIcon;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _standardDisplayModeItem;
    private readonly Forms.ToolStripMenuItem _compactDisplayModeItem;
    public event Action? ClaudeSetupRequested;
    public event Action? SettingsRequested;
    public event Action? RefreshRequested;
    public event Action? ToggleVisibilityRequested;
    public event Action<WidgetDisplayMode>? DisplayModeRequested;
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
        var displayModeMenu = new Forms.ToolStripMenuItem("表示モード");
        _standardDisplayModeItem = new Forms.ToolStripMenuItem("標準表示")
        {
            CheckOnClick = false,
        };
        _compactDisplayModeItem = new Forms.ToolStripMenuItem("縮小表示")
        {
            CheckOnClick = false,
        };
        _standardDisplayModeItem.Click += (_, _) => DisplayModeRequested?.Invoke(WidgetDisplayMode.Standard);
        _compactDisplayModeItem.Click += (_, _) => DisplayModeRequested?.Invoke(WidgetDisplayMode.Compact);
        displayModeMenu.DropDownItems.Add(_standardDisplayModeItem);
        displayModeMenu.DropDownItems.Add(_compactDisplayModeItem);
        menu.Items.Add(displayModeMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitRequested?.Invoke());
        _icon = new Forms.NotifyIcon { Visible = true, Text = NormalTooltip, Icon = _trayIcon, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => SettingsRequested?.Invoke();
        SetDisplayMode(WidgetDisplayMode.Standard);
    }
    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _trayIcon.Dispose();
    }

    public void SetDisplayMode(WidgetDisplayMode mode)
    {
        bool compact = mode == WidgetDisplayMode.Compact;
        _standardDisplayModeItem.Checked = !compact;
        _compactDisplayModeItem.Checked = compact;
    }

    public void SetTopmostDegraded(bool degraded) =>
        _icon.Text = degraded ? TopmostDegradedTooltip : NormalTooltip;
}
