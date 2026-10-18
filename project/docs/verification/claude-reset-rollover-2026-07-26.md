# Claude reset rollover verification — 2026-07-26

この記録にはCLI生画面、使用率、account情報、認証情報を含めない。

## 実装契約

- 5時間・7日windowのreset候補を共通policyで検証
- 過去の日付なし時刻、horizon外、未対応形式、未対応zoneをfail-closed化
- 汚染済みcurrentから妥当なincomingへのmerger回復
- 拒否したmergeで `TakenAt` / `ReceivedAt` / `LastSuccessfulAt` を更新しない
- reset表示を東京時間のカレンダー日基準へ変更
- provider/account単位の正常取得日時とseverity表示を追加

## 検証

実施日時: 2026-07-26T17:54:57+09:00

- Claude CLI parser対象テスト: 28件成功、失敗0
  - 過去・同時刻、5時間／7日上限、正当な年跨ぎ、未対応英語形式、zone、
    日本語minutes-only、overflow、型付き非機微診断contextを検証
- Claude merger対象テスト: 39件成功、失敗0
  - 初回horizon外incoming拒否、5時間／7日汚染currentからの回復、
    一部window拒否時のsnapshot freshness非更新、正常取得歴なしのtimestamp非生成を検証
- Codex coordinator対象テスト: 32件成功、失敗0
  - reason別継承・消去表と、`SIGNED_OUT`後の`RPC_FAILURE`で旧timestampを復活させないことを検証
- Core window／formatter対象テスト: 20件成功、失敗0
- App ViewModel／layout／display／複数account対象テスト: 80件成功、失敗0
- solution全体: 422件成功、失敗0、skip 0
- build contract: 成功
- coverage: 91.11%（2315 / 2541 lines）
  - 対象: Core、Codex、Claude、Claude.Windows、Windows
  - 結果: `TestResults/coverage-dd28241b14894c1ab0bfb542e80ea598/`
- `dotnet format .\CodexUsageMonitor.slnx --verify-no-changes`: 成功
- `dotnet build .\CodexUsageMonitor.slnx -c Release`: 成功（warning 0、error 0）
- Core以外のsourceに5時間／7日windowの`300` / `10080`定義が残っていないことを静的確認
  （refresh intervalの300秒は別契約）
- `UsageViewModel`に旧3色のインライン`FromRgb`生成が残っていないことを静的確認
- reset診断は型付きcategory、zone category、delta分、window期間だけを保持し、
  生画面、使用率、account、絶対日時を保持しないことをテストと静的確認で検証

## 未実施

- 実アカウントを使用するreset境界の手動受入は実施していない。
- workspace外の配布済みEXE、起動中アプリ、自動起動設定は変更していない。
- CLI生画面、使用率、account情報、認証情報は取得・保存していない。

## 再検証

実施日時: 2026-07-26T21:51:17+09:00 以降

- 計画対象テスト: 204件成功、失敗0
  - Claude CLI parser: 29件
  - Core window policy／reset・freshness formatter: 20件
  - Claude merger: 39件
  - Codex coordinator: 32件
  - App ViewModel／layout／display／複数account: 84件
- solution全体: 428件成功、失敗0、skip 0
- build contract: 成功
- coverage: 91.15%（2339 / 2566 lines）
  - 対象: Core、Codex、Claude、Claude.Windows、Windows
  - 結果: `TestResults/coverage-f1b5f59a28d64b878cef9d59c1aaf9d9/`
- `dotnet format .\CodexUsageMonitor.slnx --verify-no-changes --no-restore`: 成功
- `dotnet build .\CodexUsageMonitor.slnx -c Release --no-restore`: 成功（warning 0、error 0）

## プラン乖離の是正確認

実施日時: 2026-07-26T22:04:10+09:00 以降

- 5時間枠のTokyo time-only表記は、翌日候補が5時間＋2分以内の場合だけ
  cross-midnight resetとして受理する後発契約であることをADR 003へ追記
  - 週間枠は翌日へ繰り上げない
  - `Resets 2pm`を約24時間後へ送る候補はhorizon外として拒否する
  - parser実装、README、troubleshooting、テストの契約を一致させた
- 正常時freshnessのseverityを`Normal`とし、Codex／Claude headerおよび1行時inline表示を
  `MutedBrush`へ変更
  - failureは`Warning`／`WarnBrush`、staleは`Danger`／`DangerBrush`を維持
  - usage値、bar、resetの`AccentBrush`は変更していない
  - 後続変更（2026-07-26T22:54:37+09:00以降）で1行時inline表示は廃止し、
    利用枠の行数にかかわらずprovider headerへ固定した。現在の契約と検証結果は
    `product-rename-2026-07-26.md`の「単一利用枠時のheader固定」を参照。
- 対象テスト: parser 29件、Core freshness 6件、App ViewModel／layout 49件、すべて成功
- solution全体: 428件成功、失敗0、skip 0
- build contract: 成功
- coverage: 91.15%（2339 / 2566 lines）
  - 結果: `TestResults/coverage-c332cf31daf54233a4a4846daffd3cc5/`
- `dotnet format .\CodexUsageMonitor.slnx --verify-no-changes --no-restore`: 成功
- `dotnet build .\CodexUsageMonitor.slnx -c Release --no-restore`: 成功（warning 0、error 0）
