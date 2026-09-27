# ADR 010: macOSにおけるClaude CLI画面取得（PTY＋VT画面モデル）

## Status

Accepted (2026-09-27)。fake CLIでの自動検証は完了し、実CLIでの受入は未了。

## Context

Windowsのactiveはconsoleのscreen bufferをOSから読む（ADR 003）。macOSには同等のAPIがなく、疑似端末（PTY）へ出力される制御シーケンスから画面を復元する必要がある。macOSには子孫のgroup離脱を非特権で防ぐ機構（WindowsのJob Object相当）もない。

## Decision

- **信頼と起動**: 監督helperが、開いたfileに対して署名requirement（Apple anchor、identifier `com.anthropic.claude-code`、Developer ID chain、Team ID）を静的に検証し、CLIを停止状態で起動する。実行中のコードを動的に検証し、unique identityが一致してから再開する。監視アプリ自身のad-hoc署名はCLIの信頼の根拠にしない。
- **隔離**: Windowsと同じ隔離引数（`--setting-sources '' --tools '' --no-chrome --strict-mcp-config --safe-mode --ax-screen-reader`）と専用の空workspaceで起動する。環境変数は固定の許可リストだけにし、製品が起動したCLIが自身を更新しないよう公式の`DISABLE_AUTOUPDATER=1`を渡す。managed settingsの有無をmetadataだけで確かめ、あれば起動しない（D13）。
- **版の固定**: active取得は実測で検証した版（現在2.1.274）と隔離引数の組に限る。別の版では`CLI_VERSION_REVALIDATION_REQUIRED`で停止し、計画の再検証手順を経て検証済みの版を更新する。
- **PTY起動契約**: helperが`setsid`でsession leaderになり、PTY slaveを制御端末にする。CLIは新しいprocess groupとして停止起動し、foreground groupにしてから再開する。画面は400桁×120行とする。
- **VT画面モデル**: 自作する（D5）。実測した制御（CR、LF、CUU、CUD、CHA、EL、DECSTBM、DECSC／DECRC）だけを反映し、描画に影響しないと確認した制御は読み捨てる。それ以外は画面を無効にして値を返さない（fail-closed）。文字幅はUnicode 13.0の表で決め、未登録文字も拒否する。出力が350 ms静止した完全な画面だけを読み、`Ready`を再確認してから`/usage`を送る。
- **終了保証（D12）**: 監督helperは通常終了・取消・timeout・親の死亡でTERM→3秒→KILLとし、group内の子孫を回収する。子孫がgroup／sessionを離脱したら個別に終了し、終了時に結果（`C`／`E`／`F`）を制御socketでアプリへ返す。アプリは離脱で取得結果を破棄して`CLI_GROUP_ESCAPE_DETECTED`とし、その版を`claude-active-quarantine`へ記録して再検証まで起動しない。アプリとhelperが同時に死亡した場合は保証の対象外とし、(pid, 起動時刻)のjournalで次回起動時に回収する。
- **表示言語**: CLIの画面は英語だけとみなす（`LANG`に関わらず英語で描画されることを実測）。

## Consequences

- macOSの終了保証はWindowsのJob Objectより弱い。環境変数を消してから実行した孤児は補足できない。macOS 27ではAppleのplatform binary（`sh`、`git`等）の起動時環境変数を読めないため、それらが孤児化とgroup離脱を同時に起こした場合も補足できない。検証済みの版で離脱が観測されないことを実測で確かめ、保証をその版に限る。
- CLIの更新で新しい制御シーケンスや画面が出た場合は、誤入力せず`Unsupported`になる。
- 生の画面、利用値、account情報はディスクへ保存しない。helperのjournalにはPIDと起動時刻だけを書く。
