# 起動導線・project集約 検証記録

- 実施日: 2026-07-27（Asia/Tokyo）
- 対象: `ai-usage-monitor/`
- backup: `backups/ai-usage-monitor-layout-20260727-165935/`
- backup方式: 拡張Option B

## 実装結果

- facade rootを、利用者向けBATと`project/`の2項目へ整理した。
- 利用者向けBATからnative Windows architectureを判定するdispatcherへ相対pathで委譲する。
- x64/ARM64の既存BATは互換入口として維持し、UNC拒否と成功時の出力folder提示を追加した。
- facade rootと、その配下かつ`project/`外のfolderをrebuild出力先として拒否した。
- folder pickerからの新規folder作成を無効にした。
- active code、README、TODO、未着手active planのlive pathを新layoutへ更新した。

## Backup・移動同一性

- baseline: 4,127 files、2,272,883,281 bytes
- compact copy: 758 files、6,377,020 bytes
- compact copy SHA-256 mismatch: 0
- move直後の全file SHA-256 mismatch: 0
- post-build stable baseline: 758 files
- post-build stable差分: 意図して編集した既存7 filesのみ
- reparse point: 0
- 旧staging directory: 検出なし

`artifacts/schema-current/`、`artifacts/verification/`、直下log、`_sync-conflict-archive/`のtext資産は再生成不能資産としてcompact backupへ含めた。

## 検証

```text
dotnet --version
pwsh -NoProfile -File .\scripts\test-ai-usage-monitor.ps1
dotnet build .\AiUsageMonitor.slnx -c Release
dotnet test .\AiUsageMonitor.slnx -c Release
dotnet format .\AiUsageMonitor.slnx --verify-no-changes --no-restore
```

- .NET SDK: 10.0.301
- build contract: passed
- rebuild launcher contract: passed
- architecture routing: `PASS_X64` / `PASS_ARM64` ASCII tokenで各targetを識別
- unsupported architecture、exit code 2/3/17伝播、dispatcher/target欠落: passed
- portability fixture: fixture全体と引数pathの双方で、空白・日本語・`&`・`^`・単独`%`・`!`を使用してpassed
- solution tests: 460 passed、0 failed、0 skipped
- Release build: passed、0 warnings、0 errors
- Release test: passed
- format verify: passed
- facade runtime rejection: build contract内の再実行可能なfacade root / facade child 2 cases passed
- active scopeの旧layout参照: 0
- broad reference inventory: 126 hits（new/current/historicalを含むため0件条件ではない）
- root item count: 2
- launcher fixture residue: 0
- current code page: 932

workspace rootの`scripts/format.ps1`と`scripts/lint.ps1`は存在しないため未実行。project固有の`dotnet format --verify-no-changes`、full test、Release build/testで検証した。`plans/README.md`は具体的な旧project pathを持たず、現行pathを`TODO.md`とproject READMEへ委譲しているため変更不要と判断した。

## 実機受入の制限

- 2026-07-27、利用者がARM64 Windows実機でアプリを起動できることを確認し、facade rootのレイアウトを含めて受入済みとした。
- ARM64 routeは現在のARM64 Windows実機とarchitecture識別fixtureで確認した。
- x64 routeは`PASS_X64` fixtureで確認したが、x64実機では未実施。
- 別volumeおよびmapped driveは未実施。
- UNCはsource contractでfail-closedを確認したが、実UNC shareからの起動は未実施。
- Explorer drag-and-dropとfolder pickerの目視操作は未実施。metacharacterを含むquoted invocationは自動fixtureで確認済み。
- build成果物EXEの起動、通知領域widget、Windows自動起動、Claude Code `statusLine`の再保存は未実施。

backupは利用者受入完了まで保持し、別の明示確認なしには削除しない。
