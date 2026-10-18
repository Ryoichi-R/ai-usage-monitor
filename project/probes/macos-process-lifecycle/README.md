# macOS P0-9 プロセス生成・終了プローブ

`probe.c`はmacOSのsystem APIとfake CLIだけを使い、Claude CodeやCodexを起動しません。後続の`descendant-tracker.c`のself-testもfakeだけを使います。`--observe`モードに限り、署名検証を通った実CLIをowner承認のうえで起動します（下記）。親アプリ、監督helper、TERM/SIGHUPを無視して子をforkするfake CLIを模擬します。

```sh
clang -std=c17 -Wall -Wextra -Werror project/probes/macos-process-lifecycle/probe.c \
  -o /private/tmp/ai-usage-macos-lifecycle-probe
/private/tmp/ai-usage-macos-lifecycle-probe
```

## 実施したケース

stdioとPTYで各6ケースを実行します。helper準備完了前・準備完了後の起動前・起動後の親`SIGKILL`、cancel、50ms timeout、fake CLI稼働中の監督helper `SIGKILL`です。fakeはTERM/SIGHUPを無視し、helperは有限猶予の後にKILLへ移ります。基本12ケースは成功しました。

追加でcancelとtimeoutをstdio / PTYの各20回ずつ反復し、合計80回すべてでhelperが終了し、子・孫を含む既知process groupの消滅を確認しました。これは同一条件の反復であり、cancelとtimeoutが同時に競合する全interleavingを網羅したとは扱いません。

helperが異常終了したケースでは、親が既知process groupを回収しました。helperの直接子は親アプリから`waitpid`できないため`direct_child_reaped=unknown`と記録します。テストしたgroup自体は消滅していますが、直接子のreap主体までは証明していません。

PTY fakeでは新しいsession / process group、controlling terminal、foreground group、自身をleaderとするsession、400列×120行、canonical mode、echo、lifetime pipeとPTY master FDが継承されないことを確認しました。stdio fakeにもterminalがなくlifetime pipe FDは継承されません。

## group脱出の検出と残る判定

別のfake descendantに`setsid`を実行させました。元process groupのcleanup後も脱出したprocessが生存することを確認し、確認後にfakeを終了しました。この確認はfakeが自身のPIDをpipeで申告する試験用経路によるもので、製品が持つ検出能力ではありません。group killだけでは任意のgroup脱出を回収できないことの実測です。申告なしの検出は下記`descendant-tracker.c`で実証しました。

この節の試験ではClaude CLIを起動していません。group脱出時の受入方針と同時死亡の扱いは、2026-09-27にD12（計画書）で決定しました。PTY/FD契約は下記`descendant-tracker.c`の結果で確定しています。実CLIの`/usage`経路でgroupを離脱するかどうかは未確認のため、P0-9は引き続き部分完了です。

## 親アプリとhelperの同時強制終了

stdio / PTY双方でfake CLI開始後に親アプリと監督helperへ`SIGKILL`を送り、両ownerがいなくなった状態を作りました。どちらのmodeでも外部runnerが介入する前にprocess groupが残留し、ownerがいないためcleanupは起動しませんでした。試験runnerがgroupを回収した後は消滅を確認しています。直接子のreap主体は`unknown`です。この結果は、両ownerが同時に消えた場合の回収保証を示しません。

2026-09-27のowner判断（計画D12）により、このケースは保証対象外とし、helperのsession分離と次回起動時のsweepで緩和します（下記）。後日用の数値PGIDを保存して無条件にkillする方式は、PGID再利用時に無関係なプロセスを終了させるため採用しません。ログアウトや電源断からの復帰挙動は検証していません。

## 非協調の子孫追跡（descendant-tracker.c）

`probe.c`の後続です。fake自身の申告に頼らずに子孫を追跡し、D12の3層（group内は回収、group離脱は検出してfail-closed、同時死亡は次回起動時sweep）が機構として成立するかを確認します。self-testはfake CLIだけを使います。

```sh
clang -std=gnu17 -Wall -Wextra -Werror \
  project/probes/macos-process-lifecycle/descendant-tracker.c \
  -framework Security -framework CoreFoundation \
  -o /private/tmp/ai-usage-descendant-tracker
/private/tmp/ai-usage-descendant-tracker --selftest
```

機構は次のとおりです。

- **起動**: `POSIX_SPAWN_START_SUSPENDED | SETPGROUP | CLOEXEC_DEFAULT | SETSIGDEF | SETSIGMASK`で起動し、停止中に追跡を登録してから`SIGCONT`します。
- **追跡**: kqueueの`NOTE_FORK` / `NOTE_EXEC` / `NOTE_EXIT`と、`proc_listpids(PROC_PPID_ONLY)`による再走査で子孫を(pid, 起動時刻)付きで追跡します。session / groupが変わった子孫をescapeとして数えます。
- **孤児の補足**: launchdへ付け替えられた孤児は、次の3条件をすべて満たすものだけを補足します。
  - 同uidである
  - session開始後に起動した
  - exec時環境（`KERN_PROCARGS2`）にsession tokenを持つ

  他processの環境は、条件を満たす候補についてだけメモリ上で照合します。内容は出力しません。

- **終了**: 送信前に毎回(pid, 起動時刻)を照合します。`killpg`は、照合済みのgroup memberが生存している場合だけ送ります。
- **helperとjournal**: helperは`setsid`で自分のsessionを持ち、`SIGHUP`を無視します。追跡のたびに(pid, 起動時刻)のjournalを原子的に書き換えます。次回起動時のsweepは、同uidかつ起動時刻が一致するprocessだけを終了します。

2026-09-27にMac Studio（macOS 27.0）で3回実行し、いずれもPASS（exit 0）でした（3回目は`--observe`修正後の最終binary）。

| ケース                                                                 | 結果                                                                                     |
| ---------------------------------------------------------------------- | ---------------------------------------------------------------------------------------- |
| `setsid`でgroupを離脱する孫（stdio / PTY 各20回）                      | 検出20/20、回収後の残留0。PTYではsession内の新規group・制御端末・foreground groupも20/20 |
| 親が即時終了して孤児化する孫（env保持、30回）                          | 追跡30/30（うち孤児scanによる補足は19・9・10）、残留0                                    |
| 孤児化した孫が空環境で`/bin/sleep`をexec（30回、合否対象外の既知制限） | 残留は1回目6/30、2回目0/30、3回目3/30。競合に依存し、補足を保証できない                  |
| アプリのprocess groupへSIGKILL、helperは別session（stdio 10 / PTY 5）  | helperが回収10/10・5/5                                                                   |
| 同条件でhelperがアプリと同じgroup（旧構成、stdio 3）                   | 3/3が残留し、次回起動時のsweepで3/3回収                                                  |
| アプリとhelperの両方へSIGKILL（stdio 10 / PTY 5）                      | 全件残留（fakeはTERM/HUPを無視）し、次回起動時のsweepで10/10・5/5回収                    |
| sweepの負例                                                            | 起動時刻が一致しない2件と終了済みの1件はkillせず、sentinelは生存                         |

## 実CLIの観測（署名検証付き、2026-09-27、owner承認）

`--observe`は、実行ファイルをFDで開いて静的requirement（Apple anchor / `com.anthropic.claude-code` / Developer ID chain OID / Team ID）を検証します。そのうえで停止起動し、PIDの動的署名と`kSecCodeInfoUnique`を照合してから再開します。

- 環境変数は、HOME / USER / LOGNAME / TMPDIR / 固定PATH / LANG / TERM / session tokenだけを渡します。
- 端末の内容は出力せず、anchorの有無と件数だけを出力します。
- `AIUSAGE_OBSERVE_DIAG=1`を指定した場合に限り、usage画面以外の画面を、数字と`@`を含む語を伏せ、1,500字までに切り詰めて表示します。

対象はClaude CLI 2.1.274（`~/.local/share/claude/versions/2.1.274`）です。作業directoryには、trust画面までの観測ではowner-onlyの空一時directoryを使いました。`/usage`の観測では、利用者が自分でtrustを承認した専用の空directory（`~/Library/Application Support/AiUsageMonitor-probe/ClaudeCliWorkspace`、mode 700）を使いました。既知のmanaged-settings path（`/Library/Application Support/ClaudeCode/`配下、`/Library/Managed Preferences/`の`com.anthropic.claudecode`、`~/.claude/remote-settings.json`）はmetadata上すべて存在しませんでした。server-managed sourceが存在しないことは確認できていません（owner承認のうえで受容したリスク）。

| 起動                                                                      | 結果                                                                                                                                                                                                                                                        |
| ------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| stdio `--version`                                                         | 静的・動的検証とも0。exit 0。`2.1.274 (Claude Code)`。追跡1、escape 0、残留0                                                                                                                                                                                |
| stdio `--help`                                                            | 必要flag 7種がすべて存在。子process `security`を観測（追跡3）。escape 0、残留0                                                                                                                                                                              |
| PTY、Windowsと同じ隔離引数                                                | trust画面で停止し、入力は送っていない。DA1問い合わせ1回。`SIGTERM`後3秒以内には終了せず`SIGKILL`で回収。escape 0、残留0                                                                                                                                     |
| PTY、同上の後にmasterを閉じる                                             | 538 msで全processが終了（SIGHUP経路）。escape 0、残留0                                                                                                                                                                                                      |
| trust承認後、隔離引数のPTYで`/usage`（4回）                               | Readyを判定して`/usage`を送信し、usage画面を4/4で検出。子process `security` / `git` / `sh` / `bash` / `grep`（追跡10〜15、同時生存は最大4）はすべてgroup内で終了。masterを開いたままでは`SIGTERM`後3秒以内に終了せず`SIGKILL`で回収（4/4）。escape 0、残留0 |
| 同上の後にmasterを閉じる（5秒待機4回、60秒待機3回。usage画面は7/7で検出） | CLI本体だけが残り、hangupから約9.1秒（9,088〜9,105 ms、3/3）で自ら終了。5秒時点ではすべて生存し（4/4）、その後の`SIGTERM`では猶予内に終了。escape 0、残留0                                                                                                  |

隔離引数は`--setting-sources "" --tools "" --no-chrome --strict-mcp-config --safe-mode --ax-screen-reader`です。

**誤判定の事例**: 初版の`--observe`は、Ready判定の目印に`[Screen Reader Mode: on via flag]`を含めていました。この行はtrust画面の前にも出力されるため、trust画面をReadyと誤判定し、`/usage`・Enter・Escを送りました。入力にy/nが含まれないためtrustは承認されず、CLIは「Please answer y or n.」を表示した後、Escでexit 0になりました。`~/.claude.json`に作業directoryのtrust記録がなく、更新時刻も実行前のままであることを確認しています。

この事例を受けて、次のように修正しました。

- anchorが出た後、出力が3秒静止してから全文を再分類する
- trust / signed-out / setupを優先する
- バナー単独ではReadyにしない

製品の`ClaudeCliScreenStateMachine`はprompt boxとの併用が必要なため、同じ画面をReadyにはしません。この条件はPhase 6の回帰testにします。

**Ready画面とprobeの修正**: screen-readerモードのReady画面は、`[Screen Reader Mode: on via flag]`と`$`だけの入力行で構成され、`? for shortcuts`は出ません。このためprobeのReady判定を製品と同じ条件にしました。バナーなどのanchorに加えて、`$`だけの行または`│ >`の入力枠を必須とします。trust画面にはこの行がないため、Readyにはなりません。

**`/usage`経路の結果**: `/usage`を操作する間も、CLIはgroupを離脱しませんでした（計11回）。`/usage`の画面内容は出力も保存もしていません。helperが死亡した場合のSIGHUP経路は、Ready以降では回収まで約9秒かかります。このため、計画D12では次回起動時のsweepを主な回収手段にしています。

**version**: 同日、利用者の対話起動でauto-updateが動作し、2.1.283が`versions/`へ追加されました。launcherのリンク先は2.1.274のままです。launcherが切り替わった時点で、D12の再検証条件に該当します。
