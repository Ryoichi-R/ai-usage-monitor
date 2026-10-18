# ADR 010: macOSにおけるClaude CLI画面取得（PTY＋VT画面モデル）

## Status

Accepted (2026-09-27)、2026-10-18自動互換性確認へ更新。実測範囲は[検証記録](../verification/claude-macos-auto-compatibility-2026-10-18.md)を参照。

## Context

Windowsのactiveはconsoleのscreen bufferをOSから読む（ADR 003）。macOSには同等のAPIがなく、疑似端末（PTY）へ出力される制御シーケンスから画面を復元する必要がある。macOSには子孫のgroup離脱を非特権で防ぐ機構（WindowsのJob Object相当）もない。

## Decision

- **信頼と起動**: 監督helperが、開いたfileに対して署名requirement（Apple anchor、identifier `com.anthropic.claude-code`、Developer ID chain、Team ID）を静的に検証し、CLIを停止状態で起動する。実行中のコードを動的に検証し、unique identityが一致してから再開する。監視アプリ自身のad-hoc署名はCLIの信頼の根拠にしない。
- **隔離**: Windowsと同じ隔離引数（`--setting-sources '' --tools '' --no-chrome --strict-mcp-config --safe-mode --ax-screen-reader`）と専用の空workspaceで起動する。環境変数は固定の許可リストだけにし、製品が起動したCLIが自身を更新しないよう公式の`DISABLE_AUTOUPDATER=1`を渡す。managed settingsの有無をmetadataだけで確かめ、あれば起動しない（D13）。
- **自動互換性確認**: 固定版判定と`--help`照合を廃止。公開APIのidentifier、Team ID、digest algorithmとopaqueな`kSecCodeInfoUnique`を同じ実体の検査・`--version`・PTY起動へ結合する。32バイト固定や非公開の完全長hash APIは使わない。正常終了・Cleanの`--version`だけを実体単位でprocess内cacheし、未知の版表記は`unknown`で先へ進む。各周期の署名・実体照合・画面解析・回収確認は省略しない。
- **PTY起動契約**: helperが`setsid`でsession leaderになり、PTY slaveを制御端末にする。CLIは新しいprocess groupとして停止起動し、foreground groupにしてから再開する。画面は400桁×120行とする。
- **VT画面モデル**: 自作する（D5）。実測した制御（CR、LF、CUU、CUD、CHA、EL、DECSTBM、DECSC／DECRC）だけを反映し、描画に影響しないと確認した制御は読み捨てる。それ以外は画面を無効にして値を返さない（fail-closed）。文字幅はUnicode 13.0の表で決め、未登録文字も拒否する。出力が350 ms静止した完全な画面だけを読み、`Ready`を再確認してから`/usage`を送る。
- **終了保証（D12）**: 監督helperは通常終了・取消・timeout・親の死亡でTERM→3秒→KILLとし、追跡した子孫を回収する。Claude専用v2 protocolはReady `R2`、後始末`C/E/F`、子のexit/signal、helper異常、再開有無・検査段階を分離する。期待identity不一致はコード再開前に止め、Cleanと公式の別候補への切替を再確認できる場合だけ更新競合として次回へ進む。同一実体の不一致、離脱・監督異常・欠落結果では値を公開しない。Codexの旧protocolは維持する。
- **耐久記録**: `ClaudeCompatibility`のdirectoryは0700、fileは0600。毎回CLI起動前にpending guardとstateをflush/fsyncし、同directory内の原子的置換・directory fsync後にだけ公開する。最終化の途中で失敗しても独立guardがpendingを保持する。未完了記録は再起動・sweep成功だけでは解除しない。安全停止はbinary identity、確定非互換はidentity・assembly version・helper digest・parser契約versionへ結び付ける。64 KiB超過時に整理できるのは解除済み非互換だけ。安全・移行・未完了解除の履歴は残す。
- **再試行と寿命**: 署名・identity inspectionをbackoff判定より先に行う。失敗間隔は1/5/15/30/60分、上限60分で継続。新候補・手動更新・成功でリセット。通常確認周期と再試行期限の早い方で起床し、次回予定を設定画面に表示する。設定世代の変更・受信のみへの切替・終了時は旧取得を取消し、回収を待ち、旧結果を公開しない。
- **旧停止記録**: 版だけの`claude-active-quarantine`はpathから推測して移行しない。既存の署名確認receiptで版とidentityを対応付けた保守操作`ImportLegacyMapping`だけが、receipt digest・旧file digest・identityを保存して停止を移行する。同じidentityのコピーにも適用する。証拠がなければ全候補を移行待ちにする。旧fileを保持し、内容変更は再び移行待ちとする。専用dialogで原因調査を確認して解除すると日時と対応を保存する。
- **表示言語**: CLIの画面は英語だけとみなす（`LANG`に関わらず英語で描画されることを実測）。

## Consequences

- macOSの終了保証はWindowsのJob Objectより弱い。環境変数を消してから実行した孤児は補足できない。macOS 27ではAppleのplatform binary（`sh`、`git`等）の起動時環境変数を読めないため、それらが孤児化とgroup離脱を同時に起こした場合も補足できない。自動確認は一回の取得で異常を観測しなかったことだけを示し、未知版の将来の挙動やすべての子孫の封じ込めを保証しない。
- CLIの更新で新しい制御シーケンスや画面が出た場合は、誤入力せず`Unsupported`になる。
- 生の画面、利用値、account情報はディスクへ保存しない。helperのjournalにはPIDと起動時刻だけを書く。
