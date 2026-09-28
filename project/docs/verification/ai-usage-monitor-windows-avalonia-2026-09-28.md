# Windows Avalonia 受入: 最前面表示の修正（2026-09-28）

## 状態

最前面表示のコード修正と自動検証を完了。修正版の実機目視確認は未実施。D15、Phase 2全体の受入完了を意味しない。稼働版の置換、commit、pushは実施していない。

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
