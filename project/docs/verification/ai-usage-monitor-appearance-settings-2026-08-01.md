# AI Usage Monitor 外観設定・背景表示 検証記録

## 対象

`ai-usage-monitor-appearance-settings-plan.md` の外観設定、EdgeFade、保存トランザクション、最背面Split背景を実装した候補を対象とする。

## 実装証跡

- `AppSettings` にフォント、前景色、補助色、背景色、透明度、塗りつぶし方式、EdgeFade、Split背景の設定を追加。
- `AppearanceSettingsValidator`、`BackgroundPresentationPolicy`、`AppearanceGeometryCalculator` をCoreへ分離し、UIとは独立して検証。
- 設定画面に「外観」タブを追加。無効な色、透明度、フェード率、フォント名は保存前に拒否または安全な既定値へ正規化。
- 保存は `SaveAsync` 成功後にだけ実行中設定へ反映。保存失敗時は旧設定を維持。
- Inline背景はメインウィンドウ内で描画し、EdgeFadeは上下左右の四辺を透明化。Split背景は非アクティブ・クリック透過の別ウィンドウで情報領域の四辺へ拡張して描画し、レイヤー修復失敗が連続した場合は背景を透明化。

## 自動検証

プロジェクトルート `C:\coding\ai-usage-monitor\project` で以下を実行した。

```powershell
pwsh -NoProfile -File .\scripts\format.ps1 -Check
pwsh -NoProfile -File .\scripts\lint.ps1 -Mode Full
dotnet test .\tests\AiUsageMonitor.Core.Tests\AiUsageMonitor.Core.Tests.csproj -c Release --no-restore
```

結果:

- project-local formatter: PASS
- project-local full lint/build: PASS（警告0、エラー0）
- Core tests: PASS（129件）
- Windows layer tests: PASS（22件）
- 既存の全体テスト: PASS（512件、スキップ0）
- Coverage gate: PASS（90.51%、2700/2983行、閾値90%）
- `publish-ai-usage-monitor.ps1` staging: 四辺フェードとマスク比率修正版を含む最終候補のwin-x64 / win-arm64ともにPASS。各成果物5ファイル、EXEのPE machineはそれぞれ `0x8664` / `0xAA64`。EXE SHA-256はwin-x64=`2E535C674B3A11722927FE23821937CB9C463F8A64F9E7D642A62B8D16E75486`、win-arm64=`276FD4949FF2419B3028F3964C8DC48362C38040BE09E47E78CF157CF1CB9B6C`。

主なテスト観点は既定値、ARGB正規化、異常値の保存時安全化、Inline/Split/Noneのポリシー、外形計算、JSONラウンドトリップ、既存の表示レイアウト回帰である。

## 未実施・制約

- Windowsの実デスクトップ上で、他のTopMostウィンドウ、表示倍率変更、モニター切替、タスクバー再起動を含むSplit背景の視認性を手動確認する必要がある。
- ARM64実機での起動確認は未実施。x64上のコンパイル検証とWindows向けコードの静的検証のみである。
- 保存先の既存設定に未知の外観キーがある場合も、既存の拡張データ保持契約に従い削除しない。
