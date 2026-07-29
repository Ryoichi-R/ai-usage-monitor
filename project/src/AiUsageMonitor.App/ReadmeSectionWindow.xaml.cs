using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace AiUsageMonitor.App;

public partial class ReadmeSectionWindow : Window
{
    private readonly string _readmePath;

    public ReadmeSectionWindow(string section, string readmePath)
    {
        InitializeComponent();
        ReadmeSectionBox.Text = section;
        _readmePath = Path.GetFullPath(readmePath);
    }

    private void OpenFullReadme(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_readmePath)
            {
                UseShellExecute = true,
            });
            OpenReadmeMessageText.Text = "README全体を既定のアプリで開きました。";
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            OpenReadmeMessageText.Text = $"README全体を開けませんでした: {exception.Message}";
        }
    }
}
