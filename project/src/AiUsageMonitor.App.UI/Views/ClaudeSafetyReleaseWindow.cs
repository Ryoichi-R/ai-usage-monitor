using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiUsageMonitor.App.UI.Views;

public sealed class ClaudeSafetyReleaseWindow : Window
{
    public ClaudeSafetyReleaseWindow(string reason, Func<bool> release, Action? openTroubleshooting = null)
    {
        Title = "Claude自動取得の停止解除";
        Width = 480; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Acknowledgement = new CheckBox { Content = "原因の調査と、対象プロセスの後始末を確認しました。" };
        Confirm = new Button { Content = "確認記録を残して解除", IsEnabled = false };
        Acknowledgement.IsCheckedChanged += (_, _) => Confirm.IsEnabled = Acknowledgement.IsChecked == true;
        Confirm.Click += (_, _) => { if (Acknowledgement.IsChecked == true && release()) Close(); };
        var cancel = new Button { Content = "キャンセル" }; cancel.Click += (_, _) => Close();
        var troubleshooting = new Button { Content = "対処手順を開く", IsVisible = openTroubleshooting is not null };
        troubleshooting.Click += (_, _) => openTroubleshooting?.Invoke();
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "troubleshooting文書のmacOS・互換性状態の項を確認してください。通常の更新だけでは安全停止は解除されません。", TextWrapping = TextWrapping.Wrap },
                troubleshooting, Acknowledgement, Confirm, cancel,
            },
        };
    }
    internal CheckBox Acknowledgement { get; }
    internal Button Confirm { get; }
}
