# Claude CLI 画面 fixture

`ClaudeCliScreenStateMachine` の分類テスト用 fixture。数値は `#` に伏せてある。

| ファイル | 由来 | 期待分類 |
|---|---|---|
| `trust-prompt-en.txt` | **実画面**（2026-07-24、Claude Code 2.1.218、win32-arm64 で採取） | `TrustPrompt` |
| `ready-en.txt` | 暫定 | `Ready` |
| `usage-screen-en.txt` | 暫定 | `UsageScreen` |
| `usage-screen-ja.txt` | 暫定 | `UsageScreen` |
| `signed-out-en.txt` | 暫定 | `SignedOut` |
| `setup-screen-en.txt` | 暫定 | `SetupScreen` |
| `unknown.txt` | 合成 | `Unknown` |

「暫定」の fixture は実画面から採取していない。Phase 0 の
`probes/claude-acquisition/Invoke-ClaudeAcquisitionProbe.ps1 -Scenario Screens`
で採取した `captured/*.txt` に差し替えること。差し替えるまで、これらの anchor が
実際の表示と一致する保証はない。

## macOS（`macos/`）

2026-09-27にmacOS 27.0のClaude Code 2.1.274から採取した**実画面**です。起動条件は隔離引数と`--ax-screen-reader`で、PTYの出力をメモリ上で400桁×120行の画面に復元しました。採取には[P0-3 probe](../../../probes/macos-claude-screen/README.md)を使っています。

匿名化と保存は次の順で行いました。

1. 採取したprocess内のメモリ上で匿名化する（内訳のskill名などの名前、account、`/Users/`、メールアドレス、曜日・月、time zone、数字）。
2. 禁止patternの自動検出が0件であることを確認する。
3. ownerが別sessionで見直す。
4. 見直し時のSHA-256と一致することを確かめてから保存する。

| ファイル | 数字 | 期待分類 | 用途 |
|---|---|---|---|
| `macos/ready-macos.txt` / `.parse.txt` | `#` / `1` | `Ready` | 分類 |
| `macos/usage-screen-macos.txt` | `#` | `UsageScreen` | 分類 |
| `macos/usage-screen-macos.parse.txt` | `1` | `UsageScreen` | 分類とparser（`11:11pm`、`Aug 1 at 1:11pm`の実表示形式） |
| `macos/after-escape-macos.txt` / `.parse.txt` | `#` / `1` | `Ready` | 分類（Esc後の復帰） |

`.parse.txt`は、数字をすべて`1`にした版です。parserが時刻と割合を解析できるように、行の構造を保っています。値そのものは固定のダミーです。
