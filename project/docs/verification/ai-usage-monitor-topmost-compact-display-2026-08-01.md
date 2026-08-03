# AI Usage Monitor: TopMost・Compact 表示 実装検証記録

検証日: 2026-08-01（Asia/Tokyo）

対象: `C:\coding\ai-usage-monitor\project`

## 実装概要

- `WidgetDisplayMode.Standard` / `Compact` を追加し、設定スキーマ2のまま永続化・未知値のStandard正規化・拡張データ保持を実装。
- Compact表示を150 DIP（内容幅134 DIP）、Standard表示を280 DIPとし、表示倍率・DPI変更時にもDIP基準で再配置。
- Compactの行高・縦方向の間隔はStandard相当とし、横方向だけを圧縮。文字サイズは少し小さくし、RESETと進捗バーは内容幅全体を使用。
- Compact表示ではCodex/Claudeの主要利用状況、鮮度、ステータスを残し、金額カードと補助項目を省略。
- Compact freshnessは76 DIPでellipsisを許可しつつ、同じ要素の `AutomationProperties.Name` に全文を設定し、Tooltipへ全文と非空時の出自説明を結合。
- Rootは既存のBorderを維持し、子だけを `ContentScrollViewer` と固定 `ScrollGuidanceText` の2行Gridへ変更。Compact usage rowはAuto / `*`の2列で、RESETと進捗バーを内容幅134 DIP全幅へ配置。
- Settings画面とトレイメニューから表示モードを切り替え、保存失敗時は設定とUI状態をロールバック。
- `SetWinEventHook` のForeground、MoveSizeEnd、DesktopSwitchを監視し、UIスレッド上で再入・重複を抑制して `SetWindowPos(HWND_TOPMOST, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)` を実行。
- hook登録失敗時の成功分ロールバックと次回設定適用／再表示での再登録、Win32 error code・連続失敗回数のhealth、トレイ機能低下Tooltip、250 msのeligibleAt間引き、終了時解除を実装し、フォーカス奪取APIは使用しない。
- TopMost検証用ホストをソリューションへ追加し、Phase 0のZ-order比較結果を別記録へ保存。

## 検証結果

| 検証 | 結果 |
|---|---|
| `dotnet build .\AiUsageMonitor.slnx -c Release` | PASS（警告0、エラー0） |
| `dotnet test .\AiUsageMonitor.slnx -c Release --no-build --no-restore`（是正実装後） | PASS（合計490件、失敗0） |
| `scripts/test-ai-usage-monitor.ps1`（前回基盤検証） | PASS（分離artifactのbuild/testを含む） |
| `scripts/test-ai-usage-monitor.ps1 -Coverage -Threshold 90`（前回基盤検証） | PASS（90.2%、2477/2746行。是正差分後は未再実行） |
| Phase 0 `TopmostFlagProbeTests` | PASS（両flag候補でSetWindowPos成功、dialogAboveOwner成立） |
| x64 publish | PASS、PE machine `0x8664` |
| ARM64 publish | PASS、PE machine `0xAA64` |

テスト内訳は Windows 14、Core 117、Claude 55、Codex 59、Claude.Windows 88、App 157。

## 配布候補

発行スクリプトで以下のステージング候補を生成した。既存のインストール先や正式配布ディレクトリは置換していない。

- x64: `artifacts\.staging-ai-usage-monitor-win-x64-7d5f8b5f0e9a4c16a7e2f6b5d9c31a40`
  - `AiUsageMonitor.App.exe`: 199,773,008 bytes
  - SHA-256: `0CC3C2EDA4C98B4B69EEAA708A4B8D2869FAB55FDEB9390651A7090284B77AE1`
- ARM64: `artifacts\.staging-ai-usage-monitor-win-arm64-9e4f1a2b7c6d8e0f1122334455667788`
  - `AiUsageMonitor.App.exe`: 213,695,181 bytes
  - SHA-256: `B21045745A674AC49E1C5B42CA1BE8865209171A25C4BC0F4D035BBFF618F159`

両候補で `claude-statusline-bridge.ps1`、`LICENSE`、`THIRD-PARTY-NOTICES.md` の存在を確認し、配布文書のSHA-256がソースと一致することを確認した。

## 制限・未実施

- 現在のデスクトップセッションでは、テストプロセスから外部ウィンドウへForegroundを移す操作が拒否された。そのため、外部プロセスを使う統合テストはこの制約を診断出力して終了し、Foreground移行そのものをPASSとはしていない。hook登録、再入抑制、失敗時再試行、Z-order変更API呼び出し、終了時解除は自動テストで検証済み。
- 実ユーザー設定や認証情報を読み込む正式アプリの手動起動・スクリーンショット取得は、秘密情報を含む設定を不用意に読む可能性があるため実施していない。Compactの幅、余白、列幅、表示切替、ステータス表示はWPFテストで数値検証済み。
- 共通formatterは対象パスがworkspace外のため `FORMAT_PATH_OUTSIDE_WORKSPACE` で実行拒否された。共通lintは対象パスで `inventory_digest` プロパティ欠落の内部エラーとなった。いずれもプロジェクト固有のbuild/test/coverageは完了している。
- ARM64候補はx64環境上でPEヘッダーとpublish完了までを確認したが、ARM64実機での起動確認は未実施。

## 参考

- [TopMost Phase 0 Z-order検証](topmost-zorder-phase0-2026-08-01.md)
