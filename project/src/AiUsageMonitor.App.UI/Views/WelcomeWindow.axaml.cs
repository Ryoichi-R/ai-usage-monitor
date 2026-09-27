using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AiUsageMonitor.App.UI.Views;

/// <summary>初回起動時の案内。WPF版<c>WelcomeWindow</c>の移植で、選択結果は<see cref="Completed"/>と<see cref="UseClaude"/>で返す。</summary>
public partial class WelcomeWindow : Window
{
    public WelcomeWindow()
    {
        InitializeComponent();
        if (!OperatingSystem.IsWindows())
            IntroText.Text = "最初に、表示するサービスを選んでください。後からメニューバーの「設定…」で変更できます。";
    }

    /// <summary>いずれかの開始ボタンで閉じられた場合にtrue。ウィンドウを閉じただけならfalse。</summary>
    public bool Completed { get; private set; }

    public bool UseClaude { get; private set; }

    internal void StartCodexOnly() => Finish(useClaude: false);

    internal void StartWithClaude() => Finish(useClaude: true);

    private void Finish(bool useClaude)
    {
        UseClaude = useClaude;
        Completed = true;
        Close(true);
    }

    private void OnCodexOnlyClick(object? sender, RoutedEventArgs e) => StartCodexOnly();

    private void OnClaudeClick(object? sender, RoutedEventArgs e) => StartWithClaude();
}
