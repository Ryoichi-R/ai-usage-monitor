using System.Windows;

namespace AiUsageMonitor.App;

public partial class WelcomeWindow : Window
{
    public bool UseClaude { get; private set; }

    public WelcomeWindow()
    {
        InitializeComponent();
    }

    private void StartCodexOnly(object sender, RoutedEventArgs e)
    {
        UseClaude = false;
        DialogResult = true;
    }

    private void StartWithClaude(object sender, RoutedEventArgs e)
    {
        UseClaude = true;
        DialogResult = true;
    }
}
