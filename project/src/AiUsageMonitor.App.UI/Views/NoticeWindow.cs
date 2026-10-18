using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AiUsageMonitor.App.UI.Views;

/// <summary>WPF版の<c>MessageBox</c>に代わる、OKだけを持つ通知ウィンドウ。</summary>
public sealed class NoticeWindow : Window
{
    public NoticeWindow(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Title = "AI Usage Monitor";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Message = message;
        var ok = new Button
        {
            Content = "OK",
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true,
        };
        ok.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                ok,
            },
        };
    }

    public string Message { get; }
}
