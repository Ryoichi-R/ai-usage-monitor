# ADR 011: プラットフォーム抽象境界を分離する（Platform / Platform.Windows / Claude.Cli）

## Status

Accepted (2026-08-27、Phase 1で実装。2026-09-27に文書化し、macOS実装で追加した境界を反映)

## Context

WPF版はOS依存の処理（user32のwindow層操作、registryの自動起動、Job Object、Authenticode検証、隠しconsoleの画面読取り）を、UIとClaude取得の中へ直接持っていた。macOSへ移植するには、UI基盤の選択（ADR 008）とは独立に、OS依存の処理を差し替えられる境界が必要である。境界は、UIを移す前のWindows版で振る舞いを変えずに作る（計画Phase 1）。

## Decision

- OS統合の抽象は`AiUsageMonitor.Platform`（`net10.0`）に置く。`IWidgetLayerController`、`IDisplayWorkAreaProvider`、`IStartupService`、`IExecutableTrustVerifier`、`IManagedProcessLauncher`／`IManagedProcessSession`と`IProcessLifetimeGuard`、`ISingleInstanceGuard`、`IAppPathProvider`、`IShellOpener`である。実装はOS別の`Platform.Windows`（旧`AiUsageMonitor.Windows`を改称・移設）と`Platform.Mac`に置く。
- 子プロセスは起動前に監督を準備する`IManagedProcessLauncher`で生成する。macOSでは起動後に親からprocess groupを設定できないため、起動済み`Process`への`Attach`だけの境界では移植できない。後始末の結果は`ManagedProcessOutcome`で返し、報告しない実装（WindowsのJob Object）は`Unknown`のままにする。
- Claude CLIのactive取得のうちOSに依存しない部分（`/usage`画面の状態判定とparser、取得の手順）は`AiUsageMonitor.Claude.Cli`（`net10.0`）に置く。OS別の起動と画面の読取りは`IClaudeScreenSession`／`IClaudeScreenSessionFactory`、`IClaudeWorkspaceProvisioner`、`IClaudeExecutableLocator`で注入し、共通層で具象型を生成しない。`OperatingSystem.IsWindows()`による直接判定は、注入された実装の有無による判定へ置き換える。
- 失敗理由は`ClaudeScreenFailureCode`の閉じた分類とし、既存のwire reason文字列と相互変換する。未知・欠落のreasonは`HELPER_INVALID_RESPONSE`としてfail-closedにする。対応表は契約testで固定する。managed settingsの有無の判定（`ClaudeManagedSettingsGuard`）も両OSで共有する。
- UIの`App.UI`は`Core`、`Claude`、`Claude.Cli`、`Codex`、`Platform`だけを参照し、OS別projectを参照しない。OS別の実装はthin host（`App.Windows`／`App.Mac`）のcomposition rootで`WidgetHostServices`へ注入する（D9）。参照方向は`scripts/test-build-contract.ps1`で検査する。
- macOSでrestore／buildする対象からWindows専用projectを外すため、OS別のsolution（`AiUsageMonitor.slnx`／`AiUsageMonitor.Mac.slnx`）を持つ。production assemblyはOS別のcoverage母集団へ必ず含める。

## Consequences

- OS依存の不具合はOS別projectに閉じ、共通のparser・状態判定・取得手順・表示は両OSの同じtestで検証できる。
- 新しいOS依存の処理は、まず`Platform`か`Claude.Cli`に抽象を置き、共通層から具象型を参照しない。
- WindowsのJob Objectに比べて、macOSの子プロセスの終了保証は弱い。境界の契約は同じでも、保証の範囲はOSごとに異なる（ADR 010、計画D12）。
- 利用者向けの識別子（exe名`AiUsageMonitor.App`、設定path、Mutex名、pipe名）は、境界の分離で変えない。
