# Privacy

本アプリはCodex CLIの公式App Serverを子プロセスとして起動し、OpenAIへの利用枠取得を行います。本アプリ自身は認証ファイル、cookie、アクセストークン、更新トークンを読み取り、複製、保存しません。

設定は `%LOCALAPPDATA%\CodexUsageMonitor\settings.json` に保存します。この旧製品名を含むパスは、AI Usage Monitorへの改称後も既存設定を引き継ぐ互換識別子です。複数アカウントでは利用者が付けた表示名、ローカルID、`CODEX_HOME` パス、監視・表示フラグを保存します。利用枠、追加利用額、残高、アカウントemail、認証情報は保存しません。ChatGPTアカウントの重複検出に使う非null emailはprocess内メモリだけで比較し、公開snapshot、画面、ログ、settingsへ渡しません。設定を削除する場合は、アプリ終了後にこのディレクトリをバックアップしてから削除してください。設定画面でアカウントを削除しても `CODEX_HOME` の実フォルダーは削除しません。
## Claude Code連携

Claude Code連携はopt-inです。既定の自動取得では、本アプリが署名済みの公式Claude CLIを監視アプリ専用の空フォルダーで子プロセスとして起動し、`/usage`だけを送ります。公式CLIが既存subscription認証を使ってAnthropicへ接続しますが、本アプリ自身は認証情報や非公開endpointへ触れません。

CLIのaccessibility画面はwindow矩形だけをbounded memoryで読み、既知の5時間・7日間sectionから使用済み割合とreset時刻だけを抽出します。生画面、抽出値、screenshotをログやディスクへ保存しません。cwd、transcript、repository、session ID、prompt、account情報は転送しません。

active取得では一時settingsやstatusLine bridgeを作成しません。使用率はディスクへ保存しません。専用作業フォルダーは互換性のため`%LOCALAPPDATA%\CodexUsageMonitor\ClaudeCliWorkspace`を引き続き使用します。Claude Code自身が作るsession stateはAnthropic公式CLIの管理対象であり、本アプリはユーザーの`.claude`配下を削除しません。

active `/usage`観測とpassive statusLine観測はprocess内で別々に保持しますが、どちらの使用率、raw payload、生画面、account情報もディスクへ保存しません。この変更による新しいdata収集はありません。
