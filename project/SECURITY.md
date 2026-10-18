# Security

脆弱性報告では秘密値や個人情報を添付しないでください。本アプリ本体のCodex取得経路は、解決済みのnative `codex.exe`だけを直接起動し、shell、非公開endpoint、認証情報ファイルを使用しません。

Claude自動取得は、公式install経路または利用者指定pathにあるnative `claude.exe`を絶対path化し、WinVerifyTrustと埋め込み証明書で有効な`Anthropic, PBC`署名を確認してから直接起動します。UNC・network drive、unsigned、publisher不一致の実行ファイルは起動しません。

CLIは隠しconsoleとJob Object（`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`）内で起動します。画面読取り・入力は短命な自己起動helperに分離し、応答sizeとtimeoutを制限します。入力操作は型で固定された`/usage`とEscapeだけです。信頼ダイアログ、setup、未認証、未知画面では入力せず終了します。

起動引数は`--setting-sources "" --tools "" --no-chrome --strict-mcp-config --safe-mode --ax-screen-reader`です。user/project/local scopeのCLAUDE.md、hooks、plugins、MCPを読み込ませません。managed settingsはCLI引数で上書きできない既知の制約で、画面契約を満たさない場合はfail-closedになります。

active画面parserは既知section内の`% used` / `% 使用済み`だけを認識し、0〜100の範囲、sectionの一意性、reset表示を検証します。promotionやactivityの割合は完全一致しないため採用しません。passive statusLine用bridgeは16 KiB上限と型・percentage・reset範囲の再検証を維持します。

## macOS Claudeの自動互換性確認

macOSではAppleの公開Security APIで公式署名と実体identityを静的・動的に照合してから、制限付きPTYで`/usage`を取得します。版固定や未知画面の入力許可は行いません。値を公開する条件は既知の画面契約、正常な監督・Clean回収、耐久状態の最終化です。起動前pendingはクラッシュや保存失敗後も停止を維持し、利用者が原因調査と後始末を確認する専用操作だけで解除します。利用値・生画面・任意のCLIエラー本文は永続化しません。

macOSの監督はWindows Job Objectの封じ込めと同等ではありません。環境を消去して孤児化・group離脱する子孫や、一部platform binaryを追跡できない制約があります。自動確認はこの制約を解消せず、将来版の安全保証でもありません。詳細は[ADR 010](docs/adr/010-macos-claude-cli-screen-acquisition.md)を参照してください。
