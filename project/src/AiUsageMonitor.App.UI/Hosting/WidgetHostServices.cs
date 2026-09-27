using AiUsageMonitor.App.UI.Runtime;
using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Claude.Transport;
using AiUsageMonitor.Platform;
using Avalonia.Controls;
using Avalonia.Platform;

namespace AiUsageMonitor.App.UI.Hosting;

/// <summary>
/// <see cref="WidgetHost"/>へOS別thin host（App.Windows / App.Mac）が注入する実装の組。
/// App.UIはOS別プロジェクトを参照しない（D9）ため、OS固有の処理はすべてここから受け取る。
/// </summary>
public sealed class WidgetHostServices
{
    public required IAppPathProvider AppPaths { get; init; }

    /// <summary>ログイン時自動起動（設定の<c>StartWithWindows</c>）の適用先。</summary>
    public required IStartupService Startup { get; init; }

    public required IShellOpener ShellOpener { get; init; }

    /// <summary>メインウィジェットの層（常に手前・通常）とクリック透過を所有する実装を作る。</summary>
    public required Func<IWidgetLayerController> CreateLayerController { get; init; }

    /// <summary>Claude active取得のsourceを作る。未対応のOSでは<see cref="UnavailableClaudeUsageSource"/>を返す。</summary>
    public required Func<ClaudeActiveSourceConfiguration, IClaudeUsageSource> ClaudeSourceFactory { get; init; }

    /// <summary>Claude CLI用の専用workspace。</summary>
    public required IClaudeWorkspaceProvisioner ClaudeWorkspace { get; init; }

    /// <summary>statusLine bridgeの絶対パス（active取得の一時settingsと設定例に使う）。</summary>
    public required string ClaudeBridgePath { get; init; }

    /// <summary>設定画面に表示するstatusLine設定例。nullなら表示しない。</summary>
    public string? ClaudeSetupExample { get; init; }

    /// <summary>常駐Claude Codeセッションからのpassive受信口。nullならpassive取得を行わない。</summary>
    public Func<IClaudeUsageListener>? CreateClaudeListener { get; init; }

    /// <summary>Codex app-serverの寿命管理。nullならCodexAppServerProcessの破棄処理だけで終了させる。</summary>
    public Func<System.Diagnostics.Process, IDisposable>? CodexLifetimeGuardFactory { get; init; }

    public IManagedProcessLauncher? CodexProcessLauncher { get; init; }

    /// <summary>ディスプレイの固有ID（接続順で変わらない識別子）。解決できなければnull。</summary>
    public Func<Screen, string?>? ResolveScreenStableId { get; init; }

    public required string ReadmePath { get; init; }

    /// <summary>通知領域／メニューバーのアイコン。nullならアイコンを出さない（Headless test用）。</summary>
    public WindowIcon? StatusIcon { get; init; }

    /// <summary>理由コードだけの診断。生画面・usage・accountを渡さない。</summary>
    public Action<string, Exception?> Diagnostic { get; init; } = (_, _) => { };

    public string? InheritedCodexHome { get; init; } = Environment.GetEnvironmentVariable("CODEX_HOME");

    public string UserProfile { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
