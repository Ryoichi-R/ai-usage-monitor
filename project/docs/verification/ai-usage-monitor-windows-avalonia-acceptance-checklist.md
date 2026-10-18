# Windows Avalonia実機受入チェックリスト（Windows 11 / x64）

計画Phase 2の完了条件（D15）について、Avalonia版`App.Windows`が稼働中のWPF版と同等であることを、利用者の操作・目視とClaude Codeの自動確認で確かめる。結果は各表の「結果」欄へOK / NG / 保留で記入し、NGは現象だけを書く。アカウント名・メールアドレス・実際の使用率・reset時刻・画面のスクリーンショットは記録しない（PRIVACY.md）。

- 「利用者」は操作・目視が必要な項目、「Claude」はClaude Codeがprocess・file・registry・診断ログを読み取って確認する項目。
- 比較の基準は受入前まで稼働していたWPF版（`<導入先>\AiUsageMonitorBuilds\ai-usage-monitor-win-x64`、EXE SHA-256 `E59EE496…A228`、2026-09-27 P1-W最終build）。見た目の比較が必要な項目は、試験前に同じ設定のWPF版を見ておくか、試験後にWPF版へ戻したときに見比べる。
- `<試験フォルダー>`はリポジトリ外の作業用フォルダー、`<導入先>`は導入BATで選ぶ親フォルダー（この機では稼働版の置き場所）を指す。試験用の監視script（process・window style・UI Automationのラベル有無だけを読む）も`<試験フォルダー>`に置き、リポジトリには含めない。
- Mac版の[チェックリスト](ai-usage-monitor-macos-acceptance-checklist.md)と同じく、実機で再現手段がない状態の区別は自動testの結果で代替し、その旨を結果欄に書く。

## 0. 準備（2026-09-28 Claude自動確認済み）

| 確認 | 結果 |
| --- | --- |
| 作業開始時に両repoの未コミット変更なし、`git pull --ff-only`は Already up to date（ai-usage-monitor `0c47459`、workspace-control `80e560a`） | OK |
| 実行機: Windows 11 Home 10.0.26200、OS / process architectureともX64。`ca35396`（最前面・クリック透過修正）はARM64機で検証されたもので、この機では未検証 | OK（記録のみ） |
| 稼働版はWPF（`wpfgfx_cor3.dll`をロード）。自動起動`HKCU\...\Run\CodexUsageMonitor`は稼働版EXEを指す | OK |
| 事前バックアップ: 稼働版フォルダー、共有rollback backup（`%LOCALAPPDATA%\AiUsageMonitorBackups\_backup-ai-usage-monitor-win-x64`、稼働版と同一hash）、`%LOCALAPPDATA%\CodexUsageMonitor\settings.json`（と`.bak`）、Run値を`<試験フォルダー>\backup\`へ複製し、全fileのSHA-256一致を確認（`backup-manifest.tsv`） | OK |
| 試験build: `build2`（`...\build2\AiUsageMonitorBuilds\ai-usage-monitor-win-x64\AiUsageMonitor.App.exe`、SHA-256 `5D945ABA3760E6628D3AC1AE93A9FCB8A25D7EEDC638D5D1DBA8D5A75A1609AA`）で1〜3を実施し、3-5のNG修正後に`build3`（同`...\build3\...`、SHA-256 `06BFD1320356F0EAEF7BEF49A10631D053AE877E333C9E93DA6935781B391B6B`）を作成。いずれも`rebuild-ai-usage-monitor.ps1 -Runtime win-x64 -OutputRoot <試験フォルダー>\<build>`がexit 0、PE Machine 0x8664、`licenses\`に原文9件（sourceとhash一致）、`.pdb`なし、共有rollback backupは変更なし。`build1`は`.pdb`除外前の中間buildで受入に使わない。build2とbuild3の差は常駐アイコンのダブルクリック処理（3-5）だけ | OK |
| 自動test: 全件PASS（下記の受入記録を参照）。CUM-18の`WindowsClaudeManagedSettingsTests`を含む`Claude.Windows.Tests` 45/45 PASS | OK |

**注意（共有されるもの）**: 試験版と稼働版は、Mutex、`%LOCALAPPDATA%\CodexUsageMonitor\settings.json`、Claude CLI用の専用フォルダー、statusLineのpipe名、自動起動のRun値を共有する。自動起動がONのため、**試験版を起動するとRun値が試験版のEXEへ書き換わる**（起動時に設定を適用する既存仕様。WPF版も同じ）。試験中にPCを再起動すると試験版が自動起動する。試験後にWPF版を起動するとRun値はWPF版へ戻る（10-3で確認）。

**同一build内での再試験**: 同じ`build1`〜`build3`へ2回目のrebuildを行うと、共有rollback backupが試験版で置き換わる。修正を入れて再buildする場合は`build4`など新しい親フォルダーを使う。

## 1. 起動・二重起動・終了

| # | 確認 | 担当 | 結果 |
| --- | --- | --- | --- |
| 1-1 | WPF稼働版をトレイの「終了」で終了する。`AiUsageMonitor.App`のprocessが0になる | 利用者→Claude | OK（2026-09-28 23:50頃。Claude確認: process 0） |
| 1-2 | 試験版EXEを起動する。ウィジェットとトレイアイコンが出る。ようこそ画面は出ない（既存設定を引き継ぐ） | Claude起動→利用者 | OK（build2、利用者確認。起動EXEのSHA-256一致、Run値は想定どおり試験版を指す） |
| 1-3 | 試験版の**EXEをもう一度起動**しても2つ目は起動せず、既存のウィジェットが表示される（非表示にしていた場合は再表示） | 利用者→Claude | OK（build2、Claude再現: 非表示の状態で2つ目のEXEを起動し、exit 0・96 ms、既存processの可視ウィンドウ0→1。再表示通知の受け口も外部から確認）。当初、利用者は常駐アイコンのダブルクリックで確認しており再表示されなかった→3-5 |
| 1-4 | 起動後、診断ログ`%LOCALAPPDATA%\CodexUsageMonitor\diagnostics.log`に理由コードだけが並び（利用値・account・画面文字なし）、`widget-layer-degraded`と`*-failure`がない | Claude | OK（build2、`claude-status:Available:-`の1行のみ） |

## 2. 表示・配置（WPF版と同一であること）

| # | 確認 | 担当 | 結果 |
| --- | --- | --- | --- |
| 2-1 | 標準表示: 枠なし、背景、文字・バー・色・フォント、Codex / Claude の並びがWPF版と同じ | 利用者 || OK（build2、2026-09-29 利用者確認） |
| 2-2 | トレイ「表示モード」→「縮小表示」「標準表示」で切り替わり、内容と大きさがWPF版と同じ | 利用者 || OK（build2、2026-09-29 利用者確認） |
| 2-3 | 表示倍率を75% / 150% / 200%へ変えて保存し、文字の切れ、縁のfade高さ、四隅配置の位置がWPF版と同じ（計画Phase 2の既知の意図的な差: 情報領域の高さを倍率適用後の高さで測る。100%以外で差が出ないかを見る） | 利用者 || OK（build2、2026-09-29 利用者確認） |
| 2-4 | 「四隅・余白を使用」: 基準位置（四隅）と水平・垂直余白の変更が反映される。「既定の右上へ戻す」 | 利用者 || OK（build2、2026-09-29 利用者確認） |
| 2-5 | 「自由配置」: クリック透過OFFでウィジェットをドラッグでき、離した位置が保存される（終了→起動で同じ位置） | 利用者 | OK（build3、2026-09-29 利用者確認。位置の保存・復元OK）。当初「クリック透過OFFでもドラッグできず、ON/OFFを繰り返すと一時的に直るがすぐ透過する」との報告があった。確認時点でそのprocessは終了しており原因は未特定。再起動したbuild3で約3分間、保存設定`ClickThrough`・`WS_EX_TRANSPARENT`・中心点の`WM_NCHITTEST`・その点の最前面ウィンドウを0.3秒ごとに記録し、OFF時は常にHTCLIENTかつ自ウィンドウ、ON時は背後へ通過で、設定とnative状態は常に一致し再現しなかった。稼働版切替後も観察を続ける |
| 2-6 | 「常に手前に表示」ON: 一度OFF→ONにした直後から前面に出る。Codex等の通常ウィンドウを重ねて数分操作しても隠れたままにならない。前面復帰で入力フォーカスを奪わない（`ca35396`の修正対象） | 利用者 | OK（build3、2026-09-29 利用者確認） |
| 2-7 | 「常に手前に表示」OFF: 通常ウィンドウとして他のウィンドウの後ろに回り、最前面へ強制復帰しない | 利用者 | OK（build3、2026-09-29 利用者確認。記録上も常に手前OFF時にTOPMOSTが外れた） |
| 2-8 | 背景を表示＋「他のウィンドウと重なる部分では背景だけ隠す」: 常に手前のとき、文字は前面に残り背景だけが通常ウィンドウの後ろへ分離する | 利用者 | OK（build3、2026-09-29 利用者確認） |
| 2-9 | 「クリックを透過する」ON: ウィジェット上のクリックが背後のアプリへ届く。OFF: ドラッグ・操作が戻る。再ON、背景の不透明度変更、表示／非表示の後も描画と透過が保たれる（`ca35396`の修正対象） | 利用者 | OK（build3、2026-09-29 利用者確認。監視記録でもON/OFFとnative状態が一致） |

## 3. トレイ操作

| # | 確認 | 担当 | 結果 |
| --- | --- | --- | --- |
| 3-1 | トレイアイコンのtooltipが「AI Usage Monitor」。右クリックmenuに「表示モード」「Claude Code連携…」「設定…」「今すぐ更新」「表示／非表示」「終了」があり、WPF版と同じ操作ができる | 利用者 || OK（build2、2026-09-29 利用者確認） |
| 3-2 | 「表示／非表示」でウィジェットが隠れ、もう一度で戻る | 利用者 || OK（build2、2026-09-29 利用者確認） |
| 3-3 | 「今すぐ更新」でCodex・Claudeの表示が更新される（時刻表示が変わる） | 利用者 || OK（build2、2026-09-29 利用者確認） |
| 3-4 | 「設定…」「Claude Code連携…」の画面が開き、閉じられる。README節の表示など画面内のリンクが開く | 利用者 || OK（build2、2026-09-29 利用者確認） |
| 3-5 | 常駐アイコンを**ダブルクリック**すると設定画面が開く（WPF版`TrayController`と同じ。1回のクリックでは何も起きない） | 利用者 | NG→修正→OK（build2: 何も起きない。Avalonia版は`TrayIcon.Clicked`を処理していなかった。Avalonia 12.1.2のWindows実装は左ボタンを離すたびにClickedを出しダブルクリックを区別しないため、OSのダブルクリック間隔内の2回目で設定を開くよう修正。build3で2026-09-29 利用者確認OK。macOSのメニューバーは挙動を変えない） |

## 4. 設定の保存・復元

| # | 確認 | 担当 | 結果 |
| --- | --- | --- | --- |
| 4-1 | 設定画面で複数の項目（例: 表示倍率、色1つ、背景の不透明度、縮小表示）を変えて「保存」→トレイ「終了」→再起動で復元される | 利用者 | OK（実機では2-3で倍率を保存、2-5で終了→再起動後の位置復元を利用者が確認。保存・読込の往復は`App.UI.Tests`で確認） |
| 4-2 | 設定を変えて「キャンセル」すると反映されない | 利用者 | OK（自動test `SettingsWindowTests.CancelKeepsOriginalAccountSettings`。実機では未操作） |
| 4-3 | 保存後の`settings.json`がJSONとして読め、試験後にWPF版で開いても設定が失われない（10-2で確認） | Claude | OK（全手順の後、アプリが読める状態のまま。10-2でWPF版の読込を確認） |
| 4-4 | 「Windowsログオン時に自動起動する」: 起動中はRun値が試験版EXEを指す。OFFで保存するとRun値が消え、ONで再作成される（最後にONへ戻す） | 利用者→Claude | OK（Claude確認（2026-09-29、利用者承認のうえ、試験版を強制終了して停止中に`settings.json`の該当項目だけを書き換え、再起動して確認。事前に`<試験フォルダー>\backup\settings-before-auto-checks.json`へ複製し、最後に同ファイルで戻してSHA-256一致を確認）。`StartWithWindows=false`で起動するとRun値が消え、`true`で起動するとbuild3のEXEを指して再作成された） |

## 5. ディスプレイ

| # | 確認 | 担当 | 結果 |
| --- | --- | --- | --- |
| 5-1 | 複数ディスプレイがある場合（2026-09-28時点でこの機は1画面 1920×1080と検出。なければ保留）: 「表示モニター」を変えて保存すると移り、終了→起動で同じディスプレイ・位置に戻る | 利用者 | 保留（この機は1画面。2026-09-29 利用者判断） |
| 5-2 | ディスプレイの向き（設定→ディスプレイ→画面の向き）を縦↔横に変えると、ウィジェットが作業領域内の同じ基準位置へ追従する（[回転受入](display-rotation-user-acceptance-2026-08-19.md)と同じ観点）。回転できる画面がなければ保留とし理由を書く | 利用者 | 保留（未実施。表示設定の変更を伴うため利用者判断で保留） |
| 5-3 | 解像度または拡大縮小（100/125/150%等）の変更後も、はみ出し・ぼやけがなく、基準位置へ再配置される | 利用者 | 保留（未実施。同上） |

## 6. Codex（実アカウント）

| # | 確認 | 担当 | 結果 |
| --- | --- | --- | --- |
| 6-1 | 5時間枠・週間枠と、設定でONにしている追加利用額・クレジット残高が表示される。監視アカウントを複数設定している場合は各アカウントが出る | 利用者 | OK（2-1で利用者が表示を確認。UI Automationで`CODEX`欄と残り％の表示要素を確認。値は読み出していない） |
| 6-2 | 試験版の終了後、試験版が起動したCodex app-serverが残らない（試験版の子processを起動前後で照合） | Claude | OK（build2をトレイから終了後、build2が起動したCodex app-serverとconhostは残らなかった） |

## 7. Claude（実CLI・statusLine）

| # | 確認 | 担当 | 結果 |
| --- | --- | --- | --- |
| 7-1 | 取得方法「自動（公式CLI優先、statusLineは参考）」: CLIによる取得で5時間枠・週間枠が表示され、HUDが`CLI`になる。診断ログは`claude-status:Available:-` | 利用者→Claude | OK（UI Automationで`CLAUDE`欄とHUDの`CLI`を確認。診断ログ`claude-status:Available:-`） |
| 7-2 | 通常のClaude Codeセッションを使っている間、statusLine受信でHUDが`SL受信`を示す場面がある（取得方法「statusLineのみ」に切り替えると確実） | 利用者 | OK（Claude確認（2026-09-29、利用者承認のうえ、試験版を強制終了して停止中に`settings.json`の該当項目だけを書き換え、再起動して確認。事前に`<試験フォルダー>\backup\settings-before-auto-checks.json`へ複製し、最後に同ファイルで戻してSHA-256一致を確認）。取得方法をstatusLineのみにすると`claude-status:Waiting:STATUSLINE_WAITING`。この環境ではClaude Codeからのstatusline受信が90秒間なかったため、build3同梱のbridgeへ合成入力（実利用値ではない）をWindows PowerShell 5.1で渡し、`SL受信`の表示を確認） |
| 7-3 | 公式CLIによる取得ができない状態（例: 設定の「Claude実行ファイル」に存在しないpathを入れて保存）で、statusLine受信があれば「statusLine参考値 — 最新性未保証」と表示される。確認後に空欄へ戻す | 利用者→Claude | OK（Claude確認（2026-09-29、利用者承認のうえ、試験版を強制終了して停止中に`settings.json`の該当項目だけを書き換え、再起動して確認。事前に`<試験フォルダー>\backup\settings-before-auto-checks.json`へ複製し、最後に同ファイルで戻してSHA-256一致を確認）。「Claude実行ファイル」に存在しないpathを入れると`claude-status:NotInstalled:ACTIVE_SOURCE_NOT_CLI_SCREEN`、合成statusLineの受信後に「statusLine参考値 — 最新性未保証」を表示） |
| 7-4 | 状態の区別: 起動直後の`Waiting`、7-3の失敗表示を実機で確認。Error / SignedOut / Unsupported / trust要求は実環境を変えないと再現できないため、`App.UI.Tests`（`UsageViewModelTests`・`ClaudeUsageRuntimeTests`・`UsageConvertersTests`）と`Claude.Windows.Tests`の自動testで代替する | 利用者＋自動test | OK（実機で`Waiting`（STATUSLINE_WAITING）・`NotInstalled`・`Available`を確認。Error / SignedOut / Unsupported / trust要求は自動testで代替。Claude.Windows 45件、App.UI 170件はPASS） |
| 7-5 | managed settings検査（CUM-18）: 実機では管理設定がないため能動取得は止まらない（`claude-status`が`MANAGED_SETTINGS_PRESENT`にならない）。存在時の停止は自動testで確認 | Claude＋自動test | OK（実機では`Available`で能動取得は止まらない。存在時の`MANAGED_SETTINGS_PRESENT`はWindowsの自動test `WindowsClaudeManagedSettingsTests`でPASS） |
| 7-6 | 試験版の終了後、試験版が起動した`claude.exe`（隠しconsole session）が残らない | Claude | OK（8-1の強制終了でclaude.exeの残留なし。通常の取得ではCLIは取得後に終了し、試験版の子processに残っていない） |

## 8. 異常終了時の後始末

| # | 確認 | 担当 | 結果 |
| --- | --- | --- | --- |
| 8-1 | Codex・Claudeの取得後に試験版processを強制終了し、試験版が起動した子process（Codex app-server、Claude CLI、console helper）が数秒以内に残らない（Job Objectによる回収） | Claude（利用者の承認後） | OK（Claude確認（2026-09-29、利用者承認のうえ、試験版を強制終了して停止中に`settings.json`の該当項目だけを書き換え、再起動して確認。事前に`<試験フォルダー>\backup\settings-before-auto-checks.json`へ複製し、最後に同ファイルで戻してSHA-256一致を確認）。Codex取得中の強制終了を5回、いずれもCodex app-serverとconhostの残留0。Claude CLI取得中（claude.exeとconhost 2件、codex.exeが稼働）の強制終了で10秒以内に残留0。`claude-active-quarantine`等の隔離記録なし） |

## 9. 配布物

| # | 確認 | 担当 | 結果 |
| --- | --- | --- | --- |
| 9-1 | 試験buildに`LICENSE`、`THIRD-PARTY-NOTICES.md`、`README.md`、`claude-statusline-bridge.ps1`、`licenses\`（9件、sourceと同一hash）がある | Claude | OK（0.準備で確認） |
| 9-2 | 導入BAT（`ここから開始 - AI Usage Monitorを導入・更新.bat`）がAvalonia版を配布・再構築・起動できる（計画Phase 2完了条件）。稼働版の置換を伴うため、切替の承認後に実施する | 利用者→Claude | OK（2026-09-29 00:42、利用者の承認後に利用者が導入BATを実行し`<導入先>`を選択。稼働先が置換され、EXE SHA-256はbuild3と同一の`06BFD132…1B6B`（再buildでも同じ出力）。利用者が起動し、Claude確認: Avalonia版（`libSkiaSharp`・`av_libglesv2`をload、`wpfgfx`なし）、`licenses\`9件とLICENSE・NOTICESがsourceとhash一致、`.pdb`なし、Run値が稼働版EXEを指す、`claude-status:Available:-`、子processはCodex app-serverとconhostだけ。旧WPF版は共有rollback backupへ退避（`E59EE496…A228`）。リポジトリ内に生成物なし） |

## 10. 試験後の戻し

| # | 確認 | 担当 | 結果 |
| --- | --- | --- | --- |
| 10-1 | 試験版をトレイ「終了」で終了し、WPF稼働版`<導入先>\AiUsageMonitorBuilds\ai-usage-monitor-win-x64\AiUsageMonitor.App.exe`を起動する | 利用者→Claude | 稼働版をAvalonia版へ切り替えたため、WPF版への戻しは実施せず手順を用意（受入記録「切替とロールバック」）。退避先のWPF版EXEのhashは確認済み |
| 10-2 | WPF版が試験中に保存した設定を読み、表示が崩れない。崩れる場合はバックアップの`settings.json`へ戻す（上書き前に確認する） | 利用者 | 未実施（戻しを行う場合に確認する） |
| 10-3 | Run値がWPF版EXEへ戻っている | Claude | 切替後、Run値が稼働版（Avalonia）EXEを指すことを確認 |

## 記録

2026-09-29、5-1〜5-3（保留）と10（切替のため戻しは未実施）を除く全項目がOK。結果は[Windows Avalonia受入記録](ai-usage-monitor-windows-avalonia-2026-09-28.md)の「x64実機での受入」節へ転記した。保留は計画のTODO（CUM-19）へ戻す。
