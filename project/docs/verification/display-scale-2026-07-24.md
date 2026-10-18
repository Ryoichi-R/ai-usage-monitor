# Codex Usage Monitor 可変表示倍率 検証記録

## 判定

2026-07-24 JST時点の現行ソースについて、可変表示倍率の自動検証は合格した。

物理環境に依存しない受け入れ条件は自動テストへ固定した。OS表示倍率100% / 125% / 150%と複数DPIモニター間の実機操作だけは、現在の単一画面175%環境では実施できないため、`TODO.md`の`CUM-3`として追跡する。

## 検証環境

- OS: Windows
- 接続画面数: 1
- WPF DPI倍率: 1.75（168 DPI）
- WorkingArea: 2196 x 1464 px
- 検証日時: 2026-07-24 JST

画面デバイス名などPC固有識別子は本記録へ残さない。

## 自動化した受け入れ条件

- 75 / 100 / 125 / 125.5 / 150 / 200%のWindow外形幅、Root倍率、実表示クライアント境界内包
- `SizeToContent="Height"`による変形後RootとWindow実高の一致
- TextBlockと利用率Rectangleの右端・左端・下端内包
- Codexのみ、Claudeのみ、両方表示、Loading、長いError状態の動的高さ
- TextBlockの論理`DesiredSize`がRootの利用可能幅を超えないこと
- 四隅Preset配置、Custom fraction、WorkingArea clamp
- 未ロード、ドラッグ中、終了中、Hide中の配置予約抑止
- Dispatcher予約のcoalesce、Abort、実行開始後のドラッグ再判定
- Presetの`SizeChanged`再配置とCustomの現在位置維持
- ドラッグ完了後のCustom同期
- ClickThroughの`WS_EX_TRANSPARENT`切替とTopmost切替
- 75 / 100 / 125.5 / 200の設定JSON往復、倍率欠落JSONの100%既定値
- `ja-JP` / `de-DE`での125.5表示・入力往復

層Aテストは、非表示Window自身のMeasure/ArrangeではRootの`RenderSize`が0のままになることが確認されたため、Rootを直接Measure/Arrangeする方式へ修正した。これにより内包判定が空集合で成功する状態を防いでいる。

## 実行結果

```powershell
dotnet restore .\codex-usage-monitor\CodexUsageMonitor.slnx
dotnet build .\codex-usage-monitor\CodexUsageMonitor.slnx -c Release
pwsh .\codex-usage-monitor\scripts\test-codex-usage-monitor.ps1
pwsh .\codex-usage-monitor\scripts\test-codex-usage-monitor.ps1 -Coverage -Threshold 90
dotnet format .\codex-usage-monitor\CodexUsageMonitor.slnx --verify-no-changes
```

- restore: 成功
- Release build: 成功、警告0、エラー0
- build contract: 成功
- 全テスト: 237件成功、失敗0、スキップ0
- App interactive tests: 76件を含めて成功
- coverage: 90.26%（1167 / 1293行）
- format verify: 成功

Coverage成果物:

`TestResults/coverage-d490e12aa0074251a61246ee2bfd0731/`

## ロールバック

今回の残課題修正前ベースラインは次へ保存した。

`codex-usage-monitor/.work/scale-plan-backup/remediation-pre-20260724T1620JST/`

修正前後のSHA-256は`codex-usage-monitor/.work/scale-plan-backup/hashes.md`に記録する。

初回の可変表示倍率実装より前のファイルは残っていない。既存`files/`は初回実装後スナップショットであり、初回実装全体を取り消す用途には使えない。この履歴上の制約を解消済みと偽らず、今回の残課題修正だけを確実にロールバックできる状態とする。

## 未実施の物理環境確認

次はOS設定と複数の物理画面を必要とするため、この環境では未実施である。

- OS表示倍率100% / 125% / 150%それぞれでの目視
- 異なるDPIの複数モニター間でのドラッグ
- モニター間移動後の倍率変更、位置保存、再起動
- 200%かつ両プロバイダー表示で物理WorkingArea高を超える環境の目視

実行可能になった時点で`TODO.md`の`CUM-3`に従い、本ファイルへ結果を追記する。
