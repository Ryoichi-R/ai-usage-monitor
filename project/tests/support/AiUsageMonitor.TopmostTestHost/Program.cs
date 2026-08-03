using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AiUsageMonitor.TopmostTestHost;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool topmost = args.Contains("--topmost", StringComparer.OrdinalIgnoreCase);
        string title = $"AI Usage Monitor TopMost Test Host {Environment.ProcessId}";
        var window = new Window
        {
            Title = title,
            Width = 320,
            Height = 180,
            Topmost = topmost,
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brushes.DarkSlateGray,
            Content = new TextBlock
            {
                Text = title,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var application = new Application();
        application.Run(window);
    }
}
