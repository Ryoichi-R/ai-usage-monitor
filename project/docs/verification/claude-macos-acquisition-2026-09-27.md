# macOS Claude取得の実装（2026-09-27）

MacCompositionの未対応sourceをClaudeCliActiveSourceへ置き換えた。activeとpassiveの両方を実装し、既存の取得元選択・鮮度判定・UIへ接続した。実装と自動検証が対象で、実アカウントの取得・初回trust承認・statusLine設定変更は実行していない。

## Active取得

- `Claude.Mac`が公式native CLI候補を探索し、native supervisorがApple anchor・Claude identifier・Developer ID chain・固定Team IDを検証する。開いたFD上のstatic code identityと、停止生成したprocessの動的署名・unique identityが一致してから再開する。
- 検証済みversionは2.1.274。`--version`と`--help`も同じ署名検証・stdio監督経路を通し、必要な隔離引数の存在を確認する。別versionは`CLI_VERSION_REVALIDATION_REQUIRED`で停止する。
- `openpty`、独立session、専用foreground group、制御端末、400×120、echo抑止を適用。親切断・helper死亡・TERM拒否・group離脱・次回sweepはCodexと同じ監督基盤を使用する。process journalに画面・利用値・認証情報を保存しない。
- 固定環境allowlistと専用workspace、`--setting-sources '' --tools '' --no-chrome --strict-mcp-config --safe-mode --ax-screen-reader`を適用。managed settingsは内容を読まずmetadataで有無を調べ、存在／検査不能なら停止する。対象はremote-settings、system managed-settings.json／managed-settings.d／managed-mcp.json、system／user managed preferences。
- VTは実測した許可リストだけを扱う。Unicode 13.0の固定幅表、UTF-8分割、全角・結合文字、改行・カーソル・行消去・scroll marginに対応。未対応制御・範囲外文字・不完全入力は取得を拒否し、新しいsessionで復旧する。350 ms静止と再読取りを経てReady画面だけへ`/usage`を送る。trust／setup画面へ入力しない。

## Passive取得

- 専用.NET helper `ai-usage-claude-statusline`を`.app/Contents/MacOS`へ同梱。開発buildにもコピーする。設定例はPOSIX引用した絶対パスを使う。
- 入力16 KiB上限＋超過検出、protocol=1固定、version型guard、最小payload化と出力上限再検査。接続100 ms、入力から送信まで全体1秒。失敗時は出力せずexit 0。
- `/private/tmp/aiusage-statusline-<uid>/v1.sock`を使用。親0700・socket0600・所有者・種類・symlink・peer uidを確認。directory flockで競合を防ぎ、owned stale socketだけを回復する。
- 型不正のprotocol／reset値が共通parserを例外終了させる問題も修正した。

## 自動検証と制限

署名済み偽CLIを用いて探索→能力確認→PTY→VT→共通parser→2枠取得を通し検証する。本番requirementによるad-hoc偽CLIの拒否、停止中の動的署名／コードidentity不一致拒否、stdio／PTYの異常終了matrix、入力分割・未知制御、管理設定metadata、bridgeの境界・deadline・socket差し替え・stale復旧を検証する。

次の実機確認はCodex・Claude両方を含む。activeは専用workspaceのtrustが必要で、passiveは利用者によるstatusLine設定が必要。未検証のCLI版・managed policy・未知VTは制限を緩めず停止する。server-managed policyの取得直後の空白期間は既存D13の受容リスク。両owner同時死亡の即時回収保証はなく、次回sweepで緩和する（D12）。

CLIフラグとmanaged配置は2026-09-27に[公式CLI reference](https://code.claude.com/docs/en/cli-reference)と[managed settings](https://code.claude.com/docs/en/managed-settings)で再確認した。実測・署名要件の既存証跡はmacOS P0-3／P0-6／P0-9記録を参照。
