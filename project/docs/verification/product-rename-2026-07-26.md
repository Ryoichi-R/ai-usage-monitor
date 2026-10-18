# AI Usage Monitor rename verification — 2026-07-26

この記録には利用率、account情報、認証情報、CLI生画面を含めない。

## 変更

- 表示名: `Codex Usage Monitor` → `AI Usage Monitor`
- source root / slug: `codex-usage-monitor` → `ai-usage-monitor`
- .NET root namespace / assembly: `CodexUsageMonitor` → `AiUsageMonitor`
- solution、project、test project、support project、script、batch、icon、配布output名を新名称へ統一
- CodexとClaude Codeの取得日時でtabular numeralを使用し、同じ桁数の時刻表示の開始位置を一致

## 互換識別子

既存利用者の設定・信頼承認・自動起動・bridge設定を壊さないため、次の値は旧名称のまま維持する。

- `%LOCALAPPDATA%\CodexUsageMonitor\settings.json`
- `%LOCALAPPDATA%\CodexUsageMonitor\ClaudeCliWorkspace`
- 単一起動mutex `Local\CodexUsageMonitor-5C898151`
- Windows startup registry value `CodexUsageMonitor`
- Claude statusLine pipe `CodexUsageMonitor-Claude-v1`

## ロールバック

変更前の172ファイルとworkspace `TODO.md`を
`.work/ai-usage-monitor-rename-backup/files/`へ保存し、作成時のSHA-256比較は不一致0件だった。
ロールバック時は対象pathと現在hashを再確認し、利用者の明示確認後に今回変更したpathだけを
snapshotから復元する。

## 検証

- 新solution `AiUsageMonitor.slnx`のRelease build: 成功、warning 0、error 0
- build contract: 成功
- 取得日時位置合わせtest: `取得時間 21:03`と`取得時間 21:02`のX座標一致
- UI layout / onboarding対象test: 34件成功
- startup互換test: 6件成功
- Claude workspace / pipe / hidden console互換test: 32件成功
- solution全体: 429件成功、失敗0、skip 0
- coverage: 91.16%（2342 / 2569 lines）
  - 対象: Core、Codex、Claude、Claude.Windows、Windows
  - 結果: `TestResults/coverage-b60ce451762f402ba3039da491d53385/`
- `dotnet format .\AiUsageMonitor.slnx --verify-no-changes --no-restore`: 成功

## 未実施

- workspace外の配布済みEXE、起動中アプリ、自動起動設定は変更していない
- 実アカウントを使う手動受入は実施していない
- 過去のverificationとarchive済みplan内の旧名称は履歴として書き換えていない

## 取得日時の固定列化

実施日時: 2026-07-26T22:47:38+09:00 以降

- Codex／Claude headerを共通の`122 DIP + 73 DIP`列へ変更
- `取得`の左端を各header Gridの左から122 DIPへ固定し、左揃えで描画
- device-pixel layout roundingを考慮し、実測位置が122±0.5 DIPであることを検証
- `取得時間 21:03`と`取得時間 21:02`のroot基準X座標が一致することを検証
- App test: 124件成功、失敗0
- solution全体: 429件成功、失敗0、skip 0
- build contract、format、Release build: 成功（warning 0、error 0）

## 単一利用枠時のheader固定

実施日時: 2026-07-26T22:54:37+09:00 以降

- 利用枠が1行のときだけ取得日時を利用枠行へ移すinline表示を廃止
- 利用枠の行数、追加利用額、クレジット残高、status表示の有無にかかわらず、
  取得日時をprovider headerの固定列へ表示
- 実画面と同じCodex 1行／Claude 2行の構成で、両方の取得日時がheaderに表示され、
  root基準X座標が一致し、各header内の開始位置が122±0.5 DIPであることを検証
- 原因再現test: 3件成功、失敗0
- App test: 124件成功、失敗0
- solution全体: 429件成功、失敗0、skip 0
- build contract、format、Release build: 成功（warning 0、error 0）

## 取得ラベルの明確化

実施日時: 2026-07-27T01:30:39+09:00 以降

- 正常時の`取得`を`取得時間`へ変更
- 更新失敗・期限切れ時の`最終取得`を`最終取得時間`へ変更
- Codex／Claudeとも開始位置122 DIPを維持し、右側の表示幅を63 DIPから128 DIPへ拡張
- formatter対象test: 6件成功、失敗0
- 取得時間の配置対象test: 3件成功、失敗0
- solution全体: 429件成功、失敗0、skip 0
- build contract、format、Release build: 成功（warning 0、error 0）
