using AiUsageMonitor.Core.WorkStatus;
using AiUsageMonitor.WorkStatus;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace AiUsageMonitor.App.UI.Views;

public sealed class WorkStatusSetupWindow : Window
{
    public WorkStatusSetupWindow(string stateRoot, Func<string, Task<bool>>? importLegacy = null)
    {
        Title = "作業状態の接続設定";
        Width = 660;
        Height = 560;
        var provider = new ComboBox { ItemsSource = new[] { "Codex Desktop", "Claude Code" }, SelectedIndex = 0 };
        var version = new TextBox { PlaceholderText = "Claude Codeのみ：Desktop内CLIの版（例 2.1.309）" };
        var example = new TextBox { IsReadOnly = true, AcceptsReturn = true, Height = 260, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var generate = new Button { Content = "設定例を作成する" };
        var copy = new Button { Content = "設定例をコピー", IsEnabled = false };
        var import = new Button { Content = "単体版の未確認通知を引き継ぐ…", IsEnabled = importLegacy is not null };
        import.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "旧ウィジェットを通常終了してから、その保存フォルダーを選択してください" });
            if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } path && importLegacy is not null)
                status.Text = await importLegacy(path) ? "未確認通知を引き継ぎました。旧データは保持しています。" : "引き継げません。旧版の起動中・保存権限・状態ファイルの形式を確認してください。旧データは保持しています。";
        };
        generate.Click += (_, _) =>
        {
            try
            {
                string package = Path.Combine(AppContext.BaseDirectory, "work-status-bridge");
                string managed = Path.Combine(Path.GetDirectoryName(stateRoot)!, "WorkStatusBridges");
                string bridge = BridgeDeployment.Prepare(package, managed);
                WorkProvider selected = provider.SelectedIndex == 1 ? WorkProvider.ClaudeCode : WorkProvider.CodexDesktop;
                Version? cliVersion = Version.TryParse(version.Text, out Version? parsedVersion) ? parsedVersion : null;
                example.Text = HookExamples.Create(selected, bridge, stateRoot, OperatingSystem.IsWindows(), cliVersion);
                status.Text = selected == WorkProvider.CodexDesktop
                    ? "対象プロジェクトの.codex/hooks.jsonのhooksへ追加し、Codex DesktopでReview hooksを行ってください。既存設定を置換しないでください。"
                    : "対象プロジェクトの.claude/settings.local.jsonのhooksへ追加してください。生成管理されたプロジェクトでは設定生成器を使い、既存の権限・statusLineを保持してください。";
                copy.IsEnabled = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                example.Text = "";
                copy.IsEnabled = false;
                status.Text = "設定例を作成できません。配布物のBridgeと、Claude Codeの場合は対応版の指定を確認してください。";
            }
        };
        copy.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard && example.Text is { } text) await clipboard.SetTextAsync(text);
        };
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "作業状態はプロジェクトごとに接続します。会話本文は保存しません。", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    provider, version, generate, copy, status, example, import,
                    new TextBlock { Text = "設定例だけでは接続済みになりません。設定画面で作業状態の表示を有効にし、実イベントの受信を確認してください。", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                },
            },
        };
    }
}
