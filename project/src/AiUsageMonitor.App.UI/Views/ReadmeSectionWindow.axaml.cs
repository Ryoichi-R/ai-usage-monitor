using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AiUsageMonitor.App.UI.Views;

/// <summary>READMEの該当節を表示する。WPF版<c>ReadmeSectionWindow</c>の移植。</summary>
public partial class ReadmeSectionWindow : Window
{
    private readonly string _readmePath;

    // XAMLローダー（デザイナー）用。アプリからは節と README パスを渡す。
    public ReadmeSectionWindow()
        : this(string.Empty, "README.md")
    {
    }

    public ReadmeSectionWindow(string section, string readmePath)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(readmePath);
        InitializeComponent();
        ReadmeSectionBox.Text = section;
        _readmePath = Path.GetFullPath(readmePath);
    }

    internal void OpenFullReadme()
    {
        // macOSではUseShellExecuteのProcess.Startが存在しないファイルでも例外を投げないため、先に確認する。
        if (!File.Exists(_readmePath))
        {
            OpenReadmeMessageText.Text = $"README全体を開けませんでした: ファイルが見つかりません（{_readmePath}）";
            return;
        }

        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo(_readmePath) { UseShellExecute = true });
            OpenReadmeMessageText.Text = "README全体を既定のアプリで開きました。";
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            OpenReadmeMessageText.Text = $"README全体を開けませんでした: {exception.Message}";
        }
    }

    private void OnOpenFullReadmeClick(object? sender, RoutedEventArgs e) => OpenFullReadme();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
