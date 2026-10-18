# Windows Avalonia 受入: 最前面表示の修正（2026-09-28）

## 状態

最前面表示のコード修正と自動検証を完了。修正版の実機目視確認は未実施。（2026-09-29追記: x64機での受入は末尾の「x64実機での受入」を参照。2-6で最前面表示を利用者が確認した）D15、Phase 2全体の受入完了を意味しない。稼働版の置換、commit、pushは実施していない。

## 現象と原因

「常に手前に表示」を選択しても他のウィンドウに隠れるとの報告を受けた。WindowsWidgetLayerControllerはTopmostWindowController.RecoveryRequestedを購読しておらず、前面ウィンドウ変更時の復旧が実行されていなかった。またAlwaysOnTop設定時はフック登録のみで、最前面への即時適用がなかった。

## 修正

- AlwaysOnTop設定時に最前面へ即時適用する。
- 前面ウィンドウ変更、移動・サイズ変更終了、デスクトップ切替の通知で再適用する。
- フォーカスを奪わない既存のSetWindowPosフラグを維持する。
- 再入を抑止し、設定解除後・Dispose後の通知を無視する。

## 自動検証

基準commit: `29b079f75f58a6ee29ce11d2d70ac0a3d454b37a`。以下は未コミットの修正を含む候補の結果。コマンドの実行位置は`project/`。

| 検証 | 結果 |
| --- | --- |
| Platform.Windows.Tests Release | 64 passed / 0 failed / 0 skipped |
| Windows Avalonia win-arm64 publish | 成功 |
| 候補EXEのPE Machine | 0xAA64（ARM64） |
| lint.ps1 -Mode Fast | PASS |
| git diff --check | PASS |

実行コマンド:

```powershell
dotnet test tests/AiUsageMonitor.Platform.Windows.Tests/AiUsageMonitor.Platform.Windows.Tests.csproj -c Release --artifacts-path artifacts/topmost-fix-20260928-1853/tests --logger 'trx;LogFileName=platform-windows.trx' --results-directory artifacts/topmost-fix-20260928-1853/results --nologo
pwsh -NoProfile -File scripts/publish-ai-usage-monitor.ps1 -Runtime win-arm64 -OutputDir artifacts/topmost-fix-20260928-1853/win-arm64 -BuildArtifactsRoot topmost-fix-20260928-1853/build-arm64
dotnet restore AiUsageMonitor.slnx --verbosity quiet
pwsh -NoProfile -File scripts/lint.ps1 -Mode Fast -ReceiptPath artifacts/topmost-fix-20260928-1853/lint-receipt.json
```

初回lintは既定のビルド用参照を解決できず失敗した。solution restore後の再実行は成功。テストとpublishには独立したartifactsディレクトリを使用した。

候補EXE SHA256: `C9595B30EA92F9B2A575A782C625A2A3DA5D7FE2E15AFA2F0B98D3715517156C`。

## 実機確認手順（未実施）

試験切替の承認後、共有settings.jsonをリポジトリ外へバックアップし、稼働版をトレイから終了して候補を起動する。同一Mutexのため同時起動しない。試験後は候補を終了して元の稼働版を起動する。設定復元が必要な場合は上書き前に確認する。

| 項目 | 手順 | 結果 |
| --- | --- | --- |
| 即時適用 | 常に手前を一度解除し、再設定する | 未実施 |
| 他アプリとの重なり | Codexを前面にしてウィジェットに重ね、数分操作する | 未実施 |
| 非アクティブ維持 | Codexで入力中に前面復旧しても入力フォーカスが移らないことを確認する | 未実施 |
| モード解除 | 通常表示へ変更し、最前面へ強制復帰しないことを確認する | 未実施 |
| 再起動 | 候補を終了・再起動して設定復元と最前面表示を確認する | 未実施 |

実アカウントの利用値、アカウント名、スクリーンショットは記録しない。その他のWindows受入項目、coverage、配布ライセンス同梱、診断出力、計画・TODO更新は今回の修正検証に含めていない。

## 19:02 JST 試験起動

利用者の承認を受け、共有settings.jsonをリポジトリ外のacceptance-backupsへ複製し、SHA256一致を確認した。開始時点では旧版のプロセスは検出されなかった。候補EXEのSHA256一致を確認して起動し、候補の実行パスとプロセス生存を確認した。元の配布物は上書きしていない。

computer-useは候補ウィンドウを操作対象として取得できなかった（起動APIはtargetable windowなしを報告したが、プロセス起動は確認済み）。したがって表示・重なり順はPASSとせず、利用者へCodexを重ねた状態で数分間の確認を依頼した。スクリーンショットや利用値は取得していない。

## x64実機での受入（2026-09-28〜29、Windows 11 Home 10.0.26200、X64）

計画Phase 2の完了条件（D15）について、稼働中のWPF版と比べる実機受入を行った。手順と項目別の結果は[Windows Avalonia実機受入チェックリスト](ai-usage-monitor-windows-avalonia-acceptance-checklist.md)を正とする。実アカウントの利用値・アカウント名・スクリーンショットは記録していない。上記「最前面表示の修正」と`clickthrough-fix-2026-09-28.md`の修正（`ca35396`、ARM64機で検証）を含むbuildを、このx64機で初めて実行した。

### 状態

- 自動検証: 全921件PASS、coverage 91.12%（gate母集団）。contract・lint PASS。
- 実機受入: 1〜4・6〜9の全項目OK（3-5は途中でNG→修正→OK）。5-1〜5-3（複数ディスプレイ・回転・解像度）は利用者判断で保留。
- 導入BAT（9-2）: OK。2026-09-29 00:42に利用者の承認のうえで稼働版をAvalonia版へ切り替えた（下記「切替とロールバック」）。WPF版への戻し（10）は切替のため実施していない。
- commit・pushはしていない（下記の変更はすべて未コミット）。

### 作業開始時の状態

- ai-usage-monitor `0c47459`、workspace-control `80e560a`。両repoとも未コミット変更なし、`git pull --ff-only`はAlready up to date。
- 稼働版はWPF（`<導入先>\AiUsageMonitorBuilds\ai-usage-monitor-win-x64`、EXE SHA-256 `E59EE496…A228`、P1-W最終build）。process中の`wpfgfx_cor3.dll`で確認した。
- 事前に稼働版フォルダー、共有rollback backup（`%LOCALAPPDATA%\AiUsageMonitorBackups\_backup-ai-usage-monitor-win-x64`、稼働版と同一hash）、`settings.json`、自動起動のRun値をリポジトリ外へ複製し、全fileのSHA-256一致を確認した。

### 自動検証（x64、実行位置は`project/`）

| 検証 | 結果 |
| --- | --- |
| `pwsh scripts/test-ai-usage-monitor.ps1 -Coverage -KeepOutput` | 全921件PASS（Core 158、Codex 79、Claude 62、Claude.Cli 116、Claude.Windows 45、Platform 5、Platform.Windows 72、App.UI 170、App.WpfLegacy 209、App.WpfLegacy.Startup 1、App.Windows 4）。coverage **91.12%（5,921/6,498行）**、閾値90%以上 |
| coverage内訳（gate母集団） | App.UI 91.14%、App.Windows 76.83%、Claude 94.13%、Claude.Cli 94.29%、Claude.Windows 83.51%、Codex 87.36%、Core 97.04%、Platform 100%、Platform.Windows 91.30% |
| coverage（別集計、gate外） | App.WpfLegacy 86.96%（2,248/2,585行） |
| CUM-18 | `Claude.Windows.Tests`の`WindowsClaudeManagedSettingsTests`を含む45件がWindowsでPASS |
| `scripts/test-build-contract.ps1` / `scripts/test-rebuild-launcher-contract.ps1` | PASS |
| `scripts/lint.ps1 -Mode Fast` | PASS（solutionのrestore後。restore前の初回はformat checkが参照解決に失敗する既知の事象） |

### 受入中に見つけた問題と対応

| 問題 | 対応 |
| --- | --- |
| 初回の全件testで`ClaudeManagedSettingsGuardTests.MacSourcesIncludeManagedPreferencesForSystemAndUser`がWindowsで失敗。testがmacOSのpathを`/`区切りの文字列で比較し、製品の`Path.Combine`はWindowsでは`\`を使う | test側の問題。期待値を`Path.Combine`で組み立てるよう修正（製品コードは変更なし。macOS専用のため実行OSはmacOSだけ） |
| Windowsのcoverage gateが`Production assemblies are neither measured nor explicitly excluded: AiUsageMonitor.Claude.Bridge.Mac, AiUsageMonitor.Claude.Mac`で停止。Mac側で追加したprojectがWindowsの除外一覧に未登録 | 除外理由（macOSのtest実行で検証）を付けて登録 |
| coverage gateが旧WPF版とAvalonia版を同じ`AiUsageMonitor.App`として合算し89.28%で不合格。計画の「Phase 2母集団ではApp.WpfLegacyを別集計」と不一致 | 利用者の判断で計画どおりに分離。gateは`App.WpfLegacy`を除いた母集団で判定し、legacyはmanifestの`bySource`と`separatelyReported`へ行数と率を残す（分母から黙って消さない）。あわせてWindows hostの起動・終了処理を`WindowsProductLifecycle`へ切り出してtestを追加し、App.Windowsは35.2%→76.83% |
| Windowsのpublish／rebuildが第三者ライセンス原文（`project/licenses/`の9件）を配布物へ含めていない | 共通helper `scripts/copy-ai-usage-monitor-distribution-documents.ps1`を追加。publishが`LICENSE`、`THIRD-PARTY-NOTICES.md`、`licenses\`の原文をバイト列のまま複製してSHA-256を照合し、rebuildは置換前にstaging上で再照合する。原本にない余分なfile・欠落・改変・入れ子directoryは失敗にする。build contractに、THIRD-PARTY-NOTICES.mdのリンクと同梱原文の一致、実際の複製・改ざん・余分なfile・欠落の検査を追加 |
| 配布物にSkiaSharp／HarfBuzzSharpのnative `.pdb`（計約105MB）が入っていた。`DebugType=None`はmanagedのsymbolにしか効かない | 利用者の判断で除外。`App.Windows.csproj`でpublish対象から`.pdb`を外し、rebuildはstagingに`.pdb`があれば失敗する。build contractで両方を検査。配布物は約235MB→約130MB |
| 3-5: 常駐アイコンのダブルクリックで何も起きない（WPF版は設定画面を開く） | Avalonia 12.1.2のWindows実装（`TrayIconImpl`）は左ボタンを離すたび（`WM_LBUTTONUP`）に`Clicked`を出し、ダブルクリックを区別しない。OSのダブルクリック間隔（`PlatformSettings.GetDoubleTapTime`）内の2回目のクリックで設定画面を開く`TrayDoubleClickDetector`を追加し、`WidgetHostServices.OpenSettingsOnTrayDoubleClick`でWindows hostだけ有効にした（macOSのメニューバーは挙動を変えない）。build3で利用者がOKを確認 |
| 2-5: 「クリック透過OFFでもドラッグできず、ON/OFFを繰り返すと一時的に直るがすぐ透過する」との報告 | 確認時点で該当processは終了していた。再起動したbuild3で約3分間、保存設定・`WS_EX_TRANSPARENT`・中心点の`WM_NCHITTEST`・その点の最前面ウィンドウを記録したが、設定とnative状態は常に一致し再現しなかった。利用者もその後の操作で問題なしと確認。原因は未特定のため、稼働版切替後も観察する |
| PRIVACY.mdにWindowsの診断ログ（`ca35396`で追加）の記載がない | 保存先、記録する内容（理由コードと例外の型名・HRESULTだけ）、256 KiBでの退避を追記 |

### 診断出力（依頼手順6）の判断

依頼時点では「Windows hostは`WidgetHostServices.Diagnostic`を設定していない」との前提だったが、`ca35396`で`WindowsDiagnosticLog`（`%LOCALAPPDATA%\CodexUsageMonitor\diagnostics.log`）が実装済みだった。理由コードと例外の型名・HRESULTだけを書き、例外message・stack trace・画面・利用値は書かない（codeは英数字と`-_:`以外を拒否、256 KiBで1世代退避）。受入中は`claude-status:Available:-`、`claude-status:Waiting:STATUSLINE_WAITING`、`claude-status:NotInstalled:ACTIVE_SOURCE_NOT_CLI_SCREEN`が記録され、原因の切り分けに使えた。**追加の診断出力は不要**と判断した。起動失敗時の理由コード`startup-failure`だけ、Mac版と同じく`WindowsProductLifecycle`から出すようにした。

### 利用者の承認のうえでClaude Codeが行った確認

UI操作の代わりに、試験版を強制終了して停止中に`settings.json`の`StartWithWindows`・`ClaudeUsageAcquisitionMode`・`ClaudeExecutablePath`だけを書き換え、再起動して確認した（4-4、7-2、7-3、8-1）。書き換え前に複製し、最後に同じfileで戻してSHA-256一致を確認した。画面の確認はUI Automationで要素名に特定のラベル（`CODEX`、`CLAUDE`、`CLI`、`SL受信`、`statusLine参考値`）が含まれるかだけを調べ、値は出力していない。この環境ではClaude Codeからstatusline受信が90秒間なかったため、7-2・7-3ではbuild3同梱のbridgeへ合成入力（実利用値ではない）をWindows PowerShell 5.1で渡した。

### 試験build

| build | EXE SHA-256 | 用途 |
| --- | --- | --- |
| build1 | `851072E4A3F4F01051C1E57A9EB2CB19EF0C49DD42157BD7B6F94B35B09C8200` | `.pdb`除外前の中間build（受入に使っていない） |
| build2 | `5D945ABA3760E6628D3AC1AE93A9FCB8A25D7EEDC638D5D1DBA8D5A75A1609AA` | 1〜3の受入。3-5でNG |
| build3 | `06BFD1320356F0EAEF7BEF49A10631D053AE877E333C9E93DA6935781B391B6B` | 3-5の修正を含む。2-5〜2-9、3-5、4〜8の受入 |

いずれも`rebuild-ai-usage-monitor.ps1 -Runtime win-x64 -OutputRoot <試験フォルダー>\<build>`でexit 0、PE Machine 0x8664、`licenses\`に原文9件（sourceとhash一致）、build2以降は`.pdb`なし。buildごとに新しい親フォルダーを使った（同じ出力先へ2回目のrebuildをすると、共有rollback backupが試験版で置き換わるため）。build3以降の変更は受入記録・計画の文書だけである。

### 今回の変更（未コミット）

- 配布: `project/scripts/copy-ai-usage-monitor-distribution-documents.ps1`（新規）、`publish-ai-usage-monitor.ps1`、`rebuild-ai-usage-monitor.ps1`、`src/AiUsageMonitor.App.Windows/AiUsageMonitor.App.Windows.csproj`
- 検証: `project/scripts/test-build-contract.ps1`、`test-ai-usage-monitor.ps1`
- Windows host: `src/AiUsageMonitor.App.Windows/Program.cs`、`WindowsProductLifecycle.cs`（新規）
- 常駐アイコン: `src/AiUsageMonitor.App.UI/Hosting/TrayDoubleClickDetector.cs`（新規）、`WidgetHost.cs`、`WidgetHostServices.cs`
- test: `tests/AiUsageMonitor.App.Windows.Tests/WindowsLifecycleTests.cs`（新規）、`WindowsCompositionTests.cs`、`tests/AiUsageMonitor.App.UI.Tests/TrayDoubleClickDetectorTests.cs`（新規）、`WidgetHostingTests.cs`、`tests/AiUsageMonitor.Claude.Cli.Tests/ClaudeManagedSettingsGuardTests.cs`
- 文書: `project/PRIVACY.md`、`project/THIRD-PARTY-NOTICES.md`、本記録、チェックリスト（新規）

### 切替とロールバック

2026-09-29 00:42、利用者が試験版を終了し、導入BAT（`ここから開始 - AI Usage Monitorを導入・更新.bat`）で`<導入先>`を選んで稼働版を置き換え、起動した。稼働版EXEのSHA-256は受入したbuild3と同一（`06BFD132…1B6B`）で、確認結果はチェックリスト9-2のとおり。旧WPF版はrebuildが共有rollback backup（`%LOCALAPPDATA%\AiUsageMonitorBackups\_backup-ai-usage-monitor-win-x64`、EXE SHA-256 `E59EE496…A228`）へ退避し、受入前に取ったリポジトリ外の複製も残している。

ロールバックの手順（未実施）:

1. ファイルで戻す: トレイから終了し、`<導入先>\AiUsageMonitorBuilds\ai-usage-monitor-win-x64\`を共有rollback backupまたはリポジトリ外の複製のWPF版で置き換えて（Avalonia版だけにある`licenses\`と`ai-usage-monitor.ico`は削除）、EXEのhashを確認して起動する。自動起動のRun値は起動時の設定適用で戻る。
2. 再buildで戻す: `pwsh -NoProfile -File scripts/rebuild-ai-usage-monitor.ps1 -Runtime win-x64 -OutputRoot <導入先> -UseLegacyWpf`（導入BATは`-UseLegacyWpf`を渡さないためscriptを直接実行する）。置換前のAvalonia版は共有rollback backupへ退避される。
3. 設定: WPF版で設定の読込に問題がある場合だけ、受入前に複製した`settings.json`へ戻す（上書き前に確認する）。

### 残る制約

- 5-1〜5-3（複数ディスプレイ、回転追従、解像度・拡大縮小の変更）は未確認（保留）。回転追従は2026-08-19のWPF版受入の観点で、切替後に機会があれば確認する。
- Error / SignedOut / Unsupported / trust要求の表示は、実環境を変えずに再現できないため自動testで代替した。
- `App.WpfLegacy`（86.96%）と`App.Windows`（76.83%）は単独では90%未満。gateは計画のPhase 2母集団で判定している。legacyはPhase 2受入後の削除時に別集計ごと外す。
- Mac側の4-1（未信頼時の表示）は未確認のまま。
- 2-5で報告のあった一時的なクリック透過は再現していない。稼働版で再発した場合は、診断ログと`WS_EX_TRANSPARENT`・`WM_NCHITTEST`の状態を記録して調べる。
