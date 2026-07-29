# Phase 0 probe harness: Claude 利用率の取得経路検証

`plans/old/20260724_ai-usage-monitor-claude-desktop-usage-acquisition-plan.md` の Phase 0 を実行するための
**使い捨て harness** です。製品コードではありません。

- 製品 solution (`AiUsageMonitor.slnx`) に含めない
- `scripts/build.ps1` / `scripts/test-ai-usage-monitor.ps1` の対象外
- Phase 0 完了後も再現手段として残すが、製品コードへ流用しない

## 境界

- model prompt を送らない。CLI へ送出する文字列は `/usage` と終了操作（Esc）だけ
- ワークスペース信頼ダイアログを**自動承認しない**。検出したら入力を送らず終了する
- 認証ファイル、cookie、token を読まない。`~/.claude` 配下は**パスとサイズだけ**を参照する
- receipt に使用率の実値、account 情報、session ID、生画面を含めない
- 画面 fixture は数値を `#` に伏せて `captured/` へ保存する

## 前提

- Windows / Claude Code 2.1.218 以降
- `claude.exe` が公式インストール経路にあり、Authenticode 署名の publisher が `Anthropic, PBC` であること

## 実行手順

### 1. セットアップ（利用者操作が必要）

```powershell
pwsh -NoProfile -File .\Invoke-ClaudeAcquisitionProbe.ps1 -Scenario Setup
```

表示された専用ディレクトリで利用者自身が `claude` を1回起動し、信頼ダイアログで
「1. Yes, I trust this folder」を選んでください。**probe はこの承認を代行しません。**

### 2. B1 決定実験（最優先）

```powershell
pwsh -NoProfile -File .\Invoke-ClaudeAcquisitionProbe.ps1 -Scenario B1
```

`/usage` 実行後に statusLine 入力へ `rate_limits` が載るかを判定します。
判定は probe 専用 named pipe に届いた観測だけで行うため、利用者の常駐 Claude Code セッションが
同時に動いていても誤認しません。

| 判定 | 意味 | 次の行動 |
|---|---|---|
| `GO` | 専用 pipe へ `rate_limits` が届いた | B1 を採用。画面 parser を製品経路から外す |
| `NO-GO_NO_RATE_LIMITS` | payload は届いたが `rate_limits` が null | B2（`/usage` 画面 parse）へ切り替え |
| `NO-GO_NO_PAYLOAD` | statusLine 自体が発火しなかった | 起動プロファイルを見直したうえで B2 を検討 |
| `BLOCKED_TRUST_PROMPT` | 信頼ダイアログで停止 | 手順 1 を実行する |
| `BLOCKED_SIGNED_OUT` | 未認証 | `claude` で先にサインインする |

### 3. 画面署名の採取

```powershell
pwsh -NoProfile -File .\Invoke-ClaudeAcquisitionProbe.ps1 -Scenario Screens
```

`captured/` に数値を伏せた画面テキストが保存されます。C# 側
`ClaudeCliScreenStateMachine` の fixture 更新に使用します。

### 4. 残存物の計測

```powershell
pwsh -NoProfile -File .\Invoke-ClaudeAcquisitionProbe.ps1 -Scenario Residue
```

```powershell
pwsh -NoProfile -File .\Invoke-ClaudeAcquisitionProbe.ps1 -Scenario Residue -SkipPromptHistory
```

2回の結果を比較し、`~/.claude/projects/**` と `~/.claude/history.jsonl` の増分を計測します。
結果に応じて「後始末なし / 一定期間保持 / 個別削除」を決めます。

## 出力

- receipt: `ai-usage-monitor/project/docs/verification/claude-acquisition-<scenario>-<UTC>.md`
- 画面 fixture: `probes/claude-acquisition/captured/*.txt`（数値は `#` に伏せ済み）

## ファイル

| パス | 役割 |
|---|---|
| `Invoke-ClaudeAcquisitionProbe.ps1` | エントリーポイント（シナリオ選択） |
| `lib/probe-common.ps1` | 実行ファイル解決・署名検証、残存物計測、pipe 待受、receipt 出力 |
| `lib/read-console.ps1` | `AttachConsole` + `ReadConsoleOutputCharacterW` による画面読取り（別プロセス） |
| `lib/write-console.ps1` | `WriteConsoleInput` による入力注入（別プロセス） |
| `assets/probe-statusline-bridge.ps1` | probe 専用 pipe へ最小 payload を送る statusLine bridge |
