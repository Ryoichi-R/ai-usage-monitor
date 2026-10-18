# macOS移植 是正記録（2026-09-27）

既存の移植途中の変更を保持し、ビルド不能・未接続・終了保証・検証不足を是正した。プラン全体の完了ではない。

## 変更

- Mac csprojの不正XML、Avalonia 12 API差異、OS analyzer診断を修正。
- 共通WidgetHostへ背景分離・移動時抑制・表示領域同期・終了処理を接続。Topmostはnative controllerへ集約。
- Mac Codexを専用stdio supervisor経由で起動。停止状態で生成して追跡を登録し、親／helper異常終了と次回起動時のidentity照合sweepを実装。起動timeout・取消・初期化失敗でも回収する。
- Mac排他ロックをnative flockへ統一。Apple Siliconのvariadic open ABIを避け、所有者専用ファイルを生成。
- 実行ファイルの明示override不在時に別CLIへfallbackしないよう修正。
- Windows Avaloniaホストとcompositionテスト、既定publish/rebuild経路を追加。従来WPFは`-UseLegacyWpf`で選択。
- Macの隔離テストrunner、全production project＋native helperのcoverage gate、arm64自己完結型`.app`生成・ad-hoc署名・hash manifestを追加。

## 検証

`test-ai-usage-monitor.ps1 -Coverage`でmanaged 593件、nativeのparent/helper/both kill、group escape、identity不一致、権限／symlink拒否を検証。90%以上かつ未計測production projectなしをgateとする。WindowsホストとテストはmacOS上でクロスビルド、BATは`test-rebuild-launcher-contract.ps1 -StaticOnly`のみ検証。macOSのpackage scriptはarm64とbundle全体の署名を検証する。

## 未受入

Windows実機の新ホスト互換性、MacのSpaces・複数画面・クリック透過・メニューバー・LaunchAgent・実Codex、Gatekeeper導入は未受入。ClaudeのPTY・Security.framework起動時検証・statusLine統合は未実装でUnsupportedを維持。stdio supervisorの成功をこれらの完了としない。同時owner死亡後の回収は次回sweepであり、即時回収保証ではない。group離脱時はfail-closedとするが、追跡不能な脱出の完全保証はない。

初期の実行ファイル不在テストではMacのwell-known探索が実CLIを検出して失敗した。明示overrideをauthoritativeに修正し、再実行ではfakeのみを使用した。認証内容・CLI出力は記録していない。
