# Privacy

本アプリはCodex CLIの公式App Serverを子プロセスとして起動し、OpenAIへの利用枠取得を行います。本アプリ自身は認証ファイル、cookie、アクセストークン、更新トークンを読み取り、複製、保存しません。

設定は `%LOCALAPPDATA%\CodexUsageMonitor\settings.json` に保存します。この旧製品名を含むパスは、AI Usage Monitorへの改称後も既存設定を引き継ぐ互換識別子です。複数アカウントでは利用者が付けた表示名、ローカルID、`CODEX_HOME` パス、監視・表示フラグを保存します。利用枠、追加利用額、残高、アカウントemail、認証情報は保存しません。ChatGPTアカウントの重複検出に使う非null emailはprocess内メモリだけで比較し、公開snapshot、画面、ログ、settingsへ渡しません。設定を削除する場合は、アプリ終了後にこのディレクトリをバックアップしてから削除してください。設定画面でアカウントを削除しても `CODEX_HOME` の実フォルダーは削除しません。
## Claude Code連携

Claude Code連携はopt-inです。既定の自動取得では、本アプリが署名済みの公式Claude CLIを監視アプリ専用の空フォルダーで子プロセスとして起動し、`/usage`だけを送ります。公式CLIが既存subscription認証を使ってAnthropicへ接続しますが、本アプリ自身は認証情報や非公開endpointへ触れません。

CLIのaccessibility画面は、console screen bufferのうち現在のviewport下端から遡る直近最大120行を、列0から最大400列までbounded process memoryで読み、既知の5時間・7日間sectionから使用済み割合とreset時刻だけを抽出します。生画面、抽出値、screenshotをログやディスクへ保存しません。cwd、transcript、repository、session ID、prompt、account情報を外部へ送信しません。

active取得では一時settingsやstatusLine bridgeを作成しません。使用率はディスクへ保存しません。専用作業フォルダーは互換性のため`%LOCALAPPDATA%\CodexUsageMonitor\ClaudeCliWorkspace`を引き続き使用します。Claude Code自身が作るsession stateはAnthropic公式CLIの管理対象であり、本アプリはユーザーの`.claude`配下を削除しません。

active `/usage`観測とpassive statusLine観測はprocess内で別々に保持しますが、どちらの使用率、raw payload、生画面、account情報もディスクへ保存しません。この変更による新しいdata収集はありません。

## macOS版

macOS版も、上記と同じく認証情報・使用率・生画面・account情報をディスクへ保存しません。保存先と、macOS版だけが行う処理は次のとおりです。

- 設定と状態: `~/Library/Application Support/AiUsageMonitor/`（`settings.json`、多重起動防止の`instance.lock`、Claude CLI専用の空フォルダー`ClaudeCliWorkspace`）。ログイン時の自動起動を有効にした場合だけ、`~/Library/LaunchAgents/io.github.ryoichi-r.ai-usage-monitor.plist`を作ります。
- 診断ログ: `~/Library/Logs/AiUsageMonitor/diagnostic.log`。理由コードと例外の型名だけを書き、例外messageや画面・利用値は書きません。
- 子プロセスの後始末記録: `ProcessSessions/`へ、起動したCLIとその子孫のプロセスIDと起動時刻、および追跡用のランダムな識別子だけを書きます。アプリとhelperが同時に異常終了した場合に、次回起動時に同じプロセスだけを終了するためのもので、回収後に削除します。
- 子孫プロセスの追跡: CLIの子孫が監視下から外れたかを調べるため、CLI起動後に始まった同じユーザーのプロセスのうち、親を失ったもの（親がlaunchd）に限り、起動時の引数と環境変数をメモリ上で読み、上記の識別子を含むかだけを比べます。読んだ内容は比較後に消去し、保存・記録しません。
- 子孫の離脱を検出した場合: その回の取得結果を破棄し、`claude-active-quarantine`へ検証済みCLIのversion文字列だけを書きます。再検証が済むまで、そのversionでの自動取得を止めます。
- Claude自動取得: 公式CLIを疑似端末（400桁×120行）で起動し、画面はメモリ上の文字の表へ復元して解析します。起動時の環境変数は固定の許可リストだけで、利用者の設定ファイルは読ませません。製品が起動したCLIが自身を自動更新しないよう、公式の`DISABLE_AUTOUPDATER=1`を渡します。
- statusLine受信: `/private/tmp/aiusage-statusline-<uid>/v1.sock`（親ディレクトリ0700、socket 0600、所有者・種類・接続元ユーザーを検査）で受け取ります。受け取った使用率はメモリだけに保持します。
