# P0-5a: Claude Code macOS CLI仕様の公式文書確認

確認日: 2026-09-27（Asia/Tokyo）  
確認環境: Mac Studio / Apple Silicon / macOS 27.0  
範囲: 公式文書の確認のみ。Claude Code CLIは起動していない。

## 公式文書で確認した事項

- Native installerは`~/.local/bin/claude`をlauncherとして管理し、実体は`~/.local/share/claude/versions/`配下へ置く。macOSのnative binaryはAnthropic PBC署名とApple notarizationの対象である。
- `--ax-screen-reader`は2.1.181以降で、装飾枠とanimationのないflat text rendererを要求する。
- `--safe-mode`はuser / project由来のCLAUDE.md、skills、plugins、hooks、MCP、commands、agents、status lineなどを無効化する。ただしmanaged settings由来のhooks・status line・file-suggestion commandは残る。したがって、`--safe-mode`だけでは隔離契約を満たさない。
- `--restricted`は2.1.248以降で、user / project settingsを読まず、managed settingsと明示した`--settings`だけを読む。managed settingsは引き続き適用されるため、このflagもmanaged policyの無効化にはならない。
- `--tools ""`はbuilt-in toolsを無効化するが、MCP toolsには作用しない。MCPを使わせない条件は`--disallowedTools "mcp__*"`も必要。`--no-chrome`はChrome integrationを無効化し、`--strict-mcp-config`は明示した`--mcp-config`だけをMCP server sourceとして使う。
- user settingsは`~/.claude/settings.json`、project settingsは`.claude/settings.json`、project-local settingsは`.claude/settings.local.json`。`CLAUDE_CONFIG_DIR`はuser config rootを移動する。
- macOSのdevice-managed policyは、configuration profileの`com.anthropic.claudecode` domain、および`/Library/Application Support/ClaudeCode/`配下の`managed-settings.json`・`managed-settings.d/*.json`・`managed-mcp.json`から届く。Claude.ai / gatewayのserver-managed settingsは起動時に適用されるremote sourceである。

## 実装時に守る起動条件

1. アプリ自身がCLI引数を組み立て、user/projectから引数や`--settings`を取り込まない。
2. capability probeで必要flagが存在しない、最小versionを満たさない、または結果が不明な場合は`Unsupported`にする。
3. 起動前にsystem managed files/drop-ins、managed preferences domain、cached/server-managed settingsの有無を検査する。managed sourceを検出した場合、読み取り・診断・起動をせず`Unsupported`にする。確認不能時もfail-closedにする。
4. `--safe-mode`に加え、built-in toolsを空にし、MCPとbrowser integrationを明示的に無効化する。PTY上の`/usage`操作に必要な範囲でのみ例外を認める。
5. CLI binaryの署名検証とpath/file identity固定、監督helperの生成・回収契約（P0-6、P0-9）が成立するまで、実CLIを一切起動しない。

## 実機のmanaged-policy状態（2026-09-27、内容は非参照）

`profiles status -type enrollment`の状態項目は`Enrolled via DEP: No`、`MDM enrollment: No`だった。configuration profileの一覧・内容、`com.anthropic.claudecode` managed preferences domain、Claude.ai / gatewayのserver-managed settingsは読み出していない。この結果はMDM非登録を示すだけで、ローカルconfiguration profileやserver-managed policyの不在を証明しない。

## 未確認・保留

- 現在のMac Studioにmanaged configuration profileやserver-managed policyが存在しないことの実測は、Claude CLIを実行せず、設定内容を読まずに確認できる範囲に限定する。MDM enrollmentはNoだが、他のmanaged sourceは未確認。確認できないmanaged sourceが残る場合、P0-5b / P0-3は実施しない。
- `--safe-mode`、`--restricted`、`--tools ""`、`--no-chrome`、`--strict-mcp-config`の組合せで、実CLIの`/usage`画面が意図どおり動作することはP0-5bで確認する。これらの組合せの実行結果はまだない。
- ネイティブinstallerのlauncherはauto-updateで入れ替わるため、検証済みfile identityと起動対象の同一性を保てる実装方式はP0-6で確定する。
