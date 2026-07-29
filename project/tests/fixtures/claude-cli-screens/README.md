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
