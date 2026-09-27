# macOS実機受入チェックリスト（Mac Studio / macOS 27.0）

Phase 3〜6とP0-2（Avalonia 12系）の実機受入で、利用者が操作・目視する項目の一覧。結果は各表の「結果」欄へOK / NG / 保留で記入し、NGは現象だけを書く。アカウント名・メールアドレス・実際の使用率・reset時刻・画面のスクリーンショットは記録しない（PRIVACY.md）。

## 0. 準備（2026-09-27 自動確認済み）

| 確認 | 結果 |
| --- | --- |
| `.app`生成: `artifacts/macos-20260927T125756Z/AI Usage Monitor.app`（arm64、ad-hoc署名、`codesign --verify --deep --strict` PASS、`LSUIElement=true`、`Contents/Resources/licenses/`にライセンス原文9件）。理由別の停止文言、Windows管理設定検査、VT許可リストの修正、診断ログの理由コード記録を含む版 | OK |
| 本番の監督helperで、導入済みClaude CLI 2.1.274が署名要件（Apple anchor / identifier / Developer ID / Team ID）を満たす。無関係なbinaryは拒否 | OK |
| `~/.local/bin/claude`は2.1.274を指す。managed settingsの既知path 7件はすべて不在 | OK |
| Codex: `/Applications/ChatGPT.app`同梱のCodex CLIが存在（locatorの候補） | OK |
| statusLine helperを`PATH`空・空入力で起動し、出力なし・exit 0 | OK |
| デモモード（`--demo`、設定保存・CLI起動なし）で8秒間起動し、終了後の残留なし | OK |
| 既存の状態なし（`~/Library/Application Support/AiUsageMonitor`、LaunchAgent、診断ログのいずれも未作成）。初回起動になる | OK |

**配置**: 自動起動とstatusLine設定は`.app`内の絶対パスを記録するため、publishのたびに変わる`artifacts/`から直接使わず、先に固定の場所へ複製する。

```bash
ditto "artifacts/macos-20260927T125756Z/AI Usage Monitor.app" "$HOME/Applications/AI Usage Monitor.app"
```

既に古い版を置いている場合は、`ditto`が不要になったfileを消さずに上書きするため、古い`.app`をゴミ箱へ移してから複製する。

ローカルで生成したため`com.apple.quarantine`属性はなく、通常はGatekeeperの確認は出ない。出た場合はFinderで右クリック→「開く」で許可する（D3によりnotarizationは行わない）。

**残留確認**: 終了試験の後、projectで次を実行し`residual=0`を確認する（読み取りのみ。PIDと実行ファイル名だけを表示）。

```bash
python3 scripts/check-macos-residual-processes.py
```

## 1. P0-2 再確認（Avalonia 12.1.2、デモモード）

`"$HOME/Applications/AI Usage Monitor.app/Contents/MacOS/AiUsageMonitor.App.Mac" --demo`で起動し、メニューバーのデモ用メニューで切り替える。終了はメニューの「終了」。

| # | 確認 | 結果 |
| --- | --- | --- |
| 1-1 | 枠なし・背景透過で表示される | OK（2026-09-27 利用者確認） |
| 1-2 | 「常に手前に表示」: 他アプリの前に出る。他アプリをフルスクリーンにしたときの挙動を記録 | OK（2026-09-27 利用者確認） |
| 1-3 | 「デスクトップ最背面」: デスクトップアイコンと同じ層で、通常ウィンドウの後ろになる | OK（2026-09-27 利用者確認） |
| 1-4 | 「クリックを透過」: ウィジェット上のクリックが下のウィンドウへ届く | OK（2026-09-27 利用者確認） |
| 1-5 | メニューバーのアイコンとメニューが表示・操作できる（D10の既知問題#22285: メニューバーが消えないか） | OK（2026-09-27 利用者確認） |
| 1-6 | 複数ディスプレイ・倍率（100/150/200%）で表示が崩れない | OK（2026-09-27 利用者確認） |

## 2. 製品起動とウィジェット（Phase 3）

`$HOME/Applications/AI Usage Monitor.app`をFinderから起動する。初回はようこそ画面が出る。

| # | 確認 | 結果 |
| --- | --- | --- |
| 2-1 | 設定でCodex・Claudeの表示をどちらも外して再起動しても、ウィジェットとメニューバーが起動する（取得なしでの起動。Phase 3完了条件） | OK（2026-09-27 利用者確認） |
| 2-2 | Dockに出ず、メニューバーにアイコンが出る | OK（2026-09-27 利用者確認） |
| 2-3 | 常に手前・クリック透過・表示倍率・縮小表示を設定画面で変更し、保存→終了→再起動で復元される | OK（2026-09-27 利用者確認）（再起動後の復元を含む） |
| 2-4 | ウィジェットを別ディスプレイへ移し、終了→再起動で同じディスプレイ・位置に戻る | OK（2026-09-27 利用者確認） |
| 2-5 | 二重起動しても2つ目は起動しない | OK（2026-09-27 利用者確認） |
| 2-6 | 設定の「ログイン時に自動起動する」をONにすると`~/Library/LaunchAgents/io.github.ryoichi-r.ai-usage-monitor.plist`ができ、ログアウト→ログインで起動する。OFFで消える | OK（2026-09-27 利用者確認）。Claude自動確認: 22:21にplist作成、22:35:45にlaunchd（親PID 1）からアプリが起動 |
| 2-7 | `~/Library/Logs/AiUsageMonitor/diagnostic.log`に`widget-layer-degraded`が出ていない | OK（2026-09-27 Claude自動確認、0件） |

## 3. Codex（Phase 4、実アカウント）

| # | 確認 | 結果 |
| --- | --- | --- |
| 3-1 | Codexの5時間枠・週間枠（と追加利用額）が表示される | OK（2026-09-27 利用者確認） |
| 3-2 | メニューから終了 → `residual=0` | OK（2026-09-27 Claude自動確認: Codex取得中のアプリへ終了要求を3回送り、いずれも`residual=0`） |
| 3-3 | 取得中に強制終了（アクティビティモニタで強制終了、または`kill -9 <アプリのPID>`）→ 数秒後に`residual=0` | OK（2026-09-27 22:28 Claude実施: Codex app-serverと監督helperの稼働中にアプリを`kill -9`、最初の確認時点で`residual=0`。残った後始末用directoryは次回起動時のsweepで削除） |

## 4. Claude active（Phase 6、実CLI）

1. メニューバーの「Claude Code連携…」を開き、表示されたコマンドをターミナルへ貼り付けて実行する。コマンドは専用フォルダー（`~/Library/Application Support/AiUsageMonitor/ClaudeCliWorkspace`）で、製品と同じ隔離引数でCLIを起動する。先頭の`DISABLE_AUTOUPDATER=1`は消さない（自動更新でlauncherが2.1.283へ切り替わると、active取得は再検証まで止まる）。
2. 信頼確認の画面でフォルダーを確認して承認し、Escで終了する。承認はアプリが代行しない。
3. 設定でClaude表示をONにする。

| # | 確認 | 結果 |
| --- | --- | --- |
| 4-0 | 2026-09-27 21:58、Claude連携画面で「接続しましたが、利用情報を取得できません」となった件を診断ログで調査し、VT許可リストの不足（`CSI > 0 q`、`CSI < u`）を修正した版で`claude-status:Available`、VT拒否なし、CLI残留なし、launcherは2.1.274のまま | OK（Claude自動確認） |
| 4-1 | 信頼承認前は「フォルダー信頼が必要」と表示され、CLIへ入力を送らない | 保留（2026-09-27: 提示コマンドを実行しても承認画面が出ず、アプリも承認画面を経ずに取得できたため、専用フォルダーは既に信頼済みと判断。未信頼状態を作らないと再現できない。fake CLIの自動testでは信頼画面をSetup／`CLAUDE_TRUST_REQUIRED`へ写像し入力を送らないことを確認済み） |
| 4-2 | 承認後、Claude Codeを常駐させていない状態から5時間枠・週間枠が表示され、HUDが`CLI`になる | OK（2026-09-27 利用者確認） |
| 4-3 | `~/.local/bin/claude`のリンク先が2.1.274のままである（`ls -l ~/.local/bin/claude`） | OK（2026-09-27 Claude自動確認: 取得成功後も2.1.274） |
| 4-4 | 取得を数回繰り返した後にアプリを終了 → `residual=0` | OK（2026-09-27 利用者確認） |
| 4-5 | 取得中に強制終了 → `residual=0`（CLIのSIGHUP終了は約9秒かかる場合がある。残れば次回起動時のsweepで回収されることも確認） | OK（2026-09-27 22:29 Claude実施: Claude CLI 2.1.274をPTYで取得中にアプリを`kill -9`、3秒後に`residual=0`。次回起動でsweepが後始末用directory 2件を削除し、取得は`Available`、隔離fileなし） |
| 4-6 | `~/Library/Application Support/AiUsageMonitor/claude-active-quarantine`が作られていない（作られた場合は子孫の離脱を検出したことを意味する。削除せず報告する） | OK（2026-09-27 Claude自動確認、取得成功時点。受入の最後にもう一度確認する） |

## 5. Claude passive（Phase 5、利用者のClaude設定を変更する）

既存の`statusLine`設定がある場合は、置き換える前に控えておく。

| # | 確認 | 結果 |
| --- | --- | --- |
| 5-1 | 設定画面のmacOS用設定例を`~/.claude/settings.json`へ反映し、通常のClaude Codeセッションを開くと、ウィジェットが`SL受信`を示す | OK（2026-09-27 利用者確認） |
| 5-2 | アプリを終了した状態でもClaude Codeのstatus lineが止まらない（helperは失敗時に出力なし・exit 0） | OK（2026-09-27 利用者確認） |
| 5-3 | active取得がない時間帯に「statusLine参考値 — 最新性未保証」の表示になる | OK（2026-09-27 利用者確認） |

## 記録

2026-09-27、4-1を除く全項目がOK。結果は[macOS実機受入記録](ai-usage-monitor-macos-2026-09-27.md)へ転記した。


完了後、結果を`docs/verification/ai-usage-monitor-macos-<実施日>.md`へ転記する（Phase 9）。NG・保留は計画のTODO（CUM-19）へ戻す。
