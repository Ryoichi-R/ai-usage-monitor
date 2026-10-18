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

## 追補: 調査後の是正（2026-09-27）

実装状況の調査で、D12の「離脱を検出したら取得をエラーにし、再検証までUnsupportedにする」がアプリ側へ届いていないことが分かった。監督helperは離脱を検出すると終了していたが、終了コードは子の終了コードの中継や他の失敗と区別できず、アプリも参照していなかった。次のとおり是正した。

- 監督helperは後始末の完了後、制御socketへ結果を1 byte返す（`C`: 全processを回収、`E`: 子孫がgroup／sessionを離脱、`F`: 回収を確認できない）。アプリは制御socketの送信側だけを閉じて終了を依頼し、helperの終了後にこの1 byteを読む。報告がない場合やhelperを強制終了した場合は回収失敗として扱う。
- `IManagedProcessSession.Outcome`（`Unknown` / `Clean` / `DescendantEscaped` / `SupervisionFailed`）を追加した。報告を持たないWindowsのJob Object実装は`Unknown`のままで、`Clean`とは扱わない。
- Claude取得は、画面から値を得た後でも、終了時に離脱が判明したら値を破棄して`CLI_GROUP_ESCAPE_DETECTED`（Unsupported）、回収失敗なら`PROCESS_CLEANUP_FAILED`（Error）を返す。能力確認（`--version` / `--help`）も同じ判定を行う。
- 離脱を検出したら`~/Library/Application Support/AiUsageMonitor/claude-active-quarantine`へ検証済みversion文字列だけを書き、再検証まで能力確認のCLI起動も行わない。読めない場合もfail-closedとする。ownerが再検証後にfileを削除するか、検証済みversionを更新すると解除される。
- 製品が起動するCLIの環境へ公式の`DISABLE_AUTOUPDATER=1`を加えた。環境変数を消去し利用者設定も読ませないため、利用者側で自動更新を止めていても製品のCLIには効かなかった。一次情報: https://code.claude.com/docs/en/env-vars
- Phase 3の追加コードのcoverageを90%以上にするため、起動処理を`Program.Run`と`MacProductLifecycle`へ切り出し、デモのメニュー、Finder起動、ディスプレイUUID、ObjC境界、監督helperの報告欠落・準備失敗・sweep失敗を試験した。`MacShellOpener`と`DemoWidgetHost`は起動操作を注入できるようにした（既定動作は同じ）。

検証（Mac Studio、macOS 27.0）: `scripts/test-ai-usage-monitor-macos.ps1 -Coverage`で669 tests PASS（是正前646）、native受入（stdio／PTYのparent-kill・helper-kill・both-kill・escapeを各2回、escapeでは`E`の報告を確認）PASS。production coverage 93.73%（6991/7459）、`Platform.Mac` 92.6%、`App.Mac` 91.8%、native 96.3%。`format.ps1 -Check` PASS。`AiUsageMonitor.slnx`のWindows向けクロスビルドは0エラー・0警告（Windowsでの実行試験ではない）。実CLI・実アカウントは使っていない。

## 追補: 実機で見つかったVT許可リストの不足（2026-09-27）

Mac実機受入の途中で、Claude連携画面が「接続しましたが、利用情報を取得できません」となった。表示文言からは原因が分からないため、診断ログへ次を記録するようにした（いずれも列挙値や制御の種類だけで、画面の文字・利用値・account情報を含めない）。

- `claude-status:<availability>:<reason>`: Claudeの取得状態が変わったときだけ1行（`WidgetHost`）。
- `claude-vt-rejected:<種類>`: VT画面モデルが最初に拒否した制御の種類（例: `csi:>0q`）。数値parameterは数字と区切り記号だけを残し、16文字で切る。

記録から、実CLI 2.1.274が起動直後に送る`CSI > 0 q`（XTVERSION、引数付き）と`CSI < u`（kitty keyboardのpop）で、画面全体を無効にしていたことが分かった。P0-3の取得PoCは、private marker（`?`・`>`・`<`・`=`）と終端文字の組で問い合わせ・設定を分類していたが、製品はREADME要約の代表形（`CSI > q`など）を完全一致で実装していた。製品の許可リストをPoCの集合へ合わせた。

- 問い合わせ・設定（応答しない。引数は数字だけ）: DA1（`CSI c`）、DA2（`CSI > c`）、XTVERSION（`CSI > q`）、modifyOtherKeys（`CSI > m`）、kitty keyboard（`CSI ? u` / `> u` / `< u` / `= u`）、DECSCUSR（`CSI Ps SP q`）。
- DECモード: 1、12、25、1000、1002、1003、1004、1006、2004、2026、2031（複数指定はすべてが集合内の場合だけ）。代替画面（1049/1047/47）と自動折り返し（7）は引き続き拒否する。

修正後の実機で`claude-status:Available:-`、VT拒否なし、取得後のCLI残留なし、`~/.local/bin/claude`は2.1.274のままだった。専用フォルダーは既に信頼済みだったため、未信頼時の表示（受入4-1）は未確認。
