# AI Usage Monitor

OpenAIおよびAnthropic非公式のWindows用デスクトップウィジェットです。CodexとClaude Codeの利用枠、リセット時刻、最終正常取得日時をまとめて監視します。Codexでは任意で追加利用額とクレジット残高も表示します。Codexは公式の `codex app-server` RPC、Claude Codeは公式CLIから取得し、各providerを個別に有効化できます。

## 必要条件

- Windows 11
- Codex監視を使う場合: Codex CLIまたはCodex Desktopがインストール済みで、ChatGPTアカウントへログイン済み
- Claude監視を使う場合: 公式Claude Code CLIがインストール済みで、Anthropicアカウントへログイン済み

アプリ自身は認証ファイル、ブラウザーcookie、トークンを読み取りません。非公開Web APIも使用しません。

## 開発実行

```powershell
dotnet run --project .\src\AiUsageMonitor.App\AiUsageMonitor.App.csproj
```

## 導入・安全再ビルド

`ai-usage-monitor`フォルダー直下の`ここから開始 - AI Usage Monitorを導入・更新.bat`を使用してください。Windowsのnative architectureを自動判定し、対応する自己完結型Release成果物を導入または安全に更新します。生成物には.NETランタイムが同梱されるため、Windows Desktop Runtimeを別途インストールする必要はありません。ビルドには.NET 10 SDKとPowerShell 7が必要です。バッチはアプリを自動起動せず、成功後に成果物フォルダーを開きます。

`project/rebuild-ai-usage-monitor-x64.bat`と`project/rebuild-ai-usage-monitor-arm64.bat`は、troubleshootingまたはdeveloper向けの互換入口です。UNC pathはサポートしません。local driveまたはmapped driveを含むdrive-letter pathへ`ai-usage-monitor`フォルダー全体を配置してください。

引数なしでダブルクリックすると保存先の既存親フォルダーを選択できます。`project`を選ぶと`project/artifacts/ai-usage-monitor-<runtime>/`へ生成します。外部の親フォルダーを選ぶと、`<選択先>/AiUsageMonitorBuilds/ai-usage-monitor-<runtime>/`へ生成します。利用者向けrootと、その配下かつ`project`外のフォルダーは選択できません。単一の既存フォルダーをバッチへドラッグ＆ドロップして指定することもできます。相対pathは`project`基準です。ファイル、存在しないパス、複数パスはbuild前に拒否します。

処理はReleaseのrestore、clean、build、OS一時領域へのstaging publish、single-file apphostのPE architecture確認を行い、すべて成功した後だけ最終出力の内容を更新します。同期フォルダ内で成果物フォルダ自体を連続リネームしないため、Google Drive等の同期クライアントと競合しません。直前の成果物は同じ管理ルートの`_backup-ai-usage-monitor-<runtime>/`へ1世代残ります。更新対象からアプリを起動している場合は、通知領域から終了してから再実行してください。更新中に失敗した場合は、その実行で作成・検証したbackupの内容だけを現行フォルダへ自動復旧します。

EXEと`claude-statusline-bridge.ps1`は同じフォルダーで保持してください。出力先を変更した場合、Windows自動起動とClaude CodeのstatusLine設定に保存された絶対パスは自動追従しません。新しいEXEを明示的に起動し、その設定画面で自動起動を保存し直してください。Claude Code連携を使う場合は、新しいEXEの設定画面から設定例を再コピーしてください。旧EXEで自動起動を保存し直すと旧パスが再登録されます。

アプリの一般設定はproject外のLocalAppDataにあり、layout移動では失われません。Claude CLI専用workspace `%LOCALAPPDATA%\CodexUsageMonitor\ClaudeCliWorkspace`も変わらないため、通常は信頼の再承認は不要です。trust promptが再表示された場合だけ、表示pathを確認して利用者自身で承認してください。

既定では幅280 DIP、右上、常に手前、クリック透過で表示します。通知領域アイコンの「設定…」は「全体設定」「表示位置」「CODEX」「CLAUDE CODE」の左タブ式です。Windowsログオン時の自動起動、CodexとClaude Codeそれぞれの使用上限表示、追加利用額、クレジット残高、表示モニター、四隅、端からの余白、Codex実行ファイル、更新間隔を変更できます。金額表示は既定OFFです。

CODEXタブでは複数アカウントを登録できます。既定アカウントは現在のCodex環境を継承し、追加アカウントには「フォルダー選択…」から、公式Codex CLIで事前にサインイン済みの別 `CODEX_HOME` 絶対パスを指定します。各アカウントの「監視」と「表示」は独立し、表示順と重複時の優先順は設定順です。有効アカウント数だけApp Serverプロセスが常駐します。同じ実効home、相対パス、存在しないhomeは別アカウントへの誤fallbackを避けるため使用しません。別homeが同じChatGPTアカウントへサインインしている場合も後続行を停止し、サインインを直した後の「今すぐ更新」で再確認します。設定行を削除しても実フォルダーは削除しません。

初回起動時は、Codexだけで開始するかClaude Code連携も設定するかを選べます。Claude Codeを選ぶと初期設定画面が開き、監視アプリ専用の空フォルダーを利用者自身が1度だけ信頼する手順と接続テストを案内します。監視アプリは信頼ダイアログを自動承認しません。後から設定する場合は、通知領域の「Claude Code連携…」または設定画面の「Claude Code連携を設定する」から同じ画面を開けます。

## Buildとテスト

```powershell
dotnet build .\AiUsageMonitor.slnx -c Release
dotnet test .\AiUsageMonitor.slnx -c Release
pwsh .\scripts\test-build-contract.ps1
pwsh .\scripts\test-ai-usage-monitor.ps1
pwsh .\scripts\publish-ai-usage-monitor.ps1 -Runtime win-x64
```

最後のpublishコマンドは開発時のpublish確認用で、後方互換の`artifacts/win-x64/`へ出力します。利用者向けの導入・更新には、安全な入れ替えと1世代backupを行うrebuildバッチを使用してください。

使用率の取得は既定300秒間隔です。Codexの定期取得はアカウントごとに独立した±10%のjitterを持ち、最初の周期取得は設定順に最大10秒ずらします。有効アカウント数に応じてApp Serverプロセス、通信、CPU・メモリ負荷も増えます。同一アカウント内の取得はsingle-flightで重複実行しません。表示値とバーは「残りメッセージ数」ではなく、providerが返す使用率から計算した残り割合であり、各行に「残り」と表示します。

## ライセンス

AI Usage Monitorの第一者コードは[MIT License](LICENSE)で提供します。
第三者コンポーネントには各コンポーネント固有のライセンスが適用され、
詳細は[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)を確認してください。

## 設定

設定画面「全体設定」タブの「表示倍率（75～200%）」で、メインウィジェットの表示サイズを変更できます。

- 表示倍率は、メインウィジェットの文字・利用率バー・余白・行高・外形を一体で75～200%に拡大縮小します。フォントだけを変える設定ではありません。
- 設定画面自体の文字サイズは変更しません。メインウィジェットの表示倍率とは分離しています。
- 反映には画面下部の「保存」が必要です。アプリの再起動は不要で、保存すると即座に反映されます。
- OSの表示スケール（Per-Monitor DPI）とは別の、アプリ内の倍率です。両者は独立して掛かります。
- 倍率を上げてメインウィジェットが作業領域より大きくなる場合、位置を作業領域内へ寄せて見出しと数値が見える状態を保ちます。このとき指定余白は維持されません。
- 複数アカウントなどで縦に長くなる場合は作業領域を上限にしてスクロールします。クリック透過中は操作できないため解除案内を表示します。通知領域の設定でクリック透過を解除してから、ウィジェット上でホイール操作してください。
- 自由配置（保存済み位置）を使っている場合、倍率変更後の位置は保存済みの相対位置から新しいウィジェットサイズに合わせて再計算されます。

追加利用額とクレジット残高はCODEXタブで個別に有効化します。金額は常に `$` と小数2桁で表示し、1セント未満の正値は `<$0.01`、無制限は `無制限` と表示します。RPCでcomponent自体が欠落した場合は0と推測せず、そのカードだけを表示しません。

### Claude Code使用上限（任意）

設定画面で「Claude Code使用上限を表示する」を有効にします。既定の「自動」は毎周期、署名済みの公式Claude CLIを隠しconsoleで起動し、`/usage`画面をアプリ内の優先観測として使います。statusLineは独立した参考値として受信し、利用可能なCLI観測がない場合だけ「最新性未保証」と明示して表示します。「今すぐ更新」も公式CLIを試行します。初回は「Claude Code連携を設定する」から専用フォルダーを利用者自身で信頼してください。

能動取得では公式CLIを`--safe-mode --ax-screen-reader`で起動して`/usage`だけを送り、既知の5時間枠・週間枠sectionから使用済み割合とresetだけを厳格に抽出します。promotion、context、activityの割合は対象にせず、両枠を一意に取得できなければ全体を失敗させます。model prompt、dummy request、tool callは送りません。アプリは認証ファイル、APIキー、OAuth token、browser cookieを読まず、Anthropicの非公開usage endpointへ直接接続しません。

`/usage`の段階描画中に見出しだけが先に現れた場合は、genericな不完全画面だけを起動timeout内で再読込します。reset形式やtime zoneの明確な不一致は従来どおりfail-closedとし、値を推測しません。

取得方法は「自動（公式CLI優先、statusLineは参考）」「statusLineのみ（受信時刻・最新性未保証）」「公式CLIの自動取得のみ」から選べます。常駐CLIのpassive statusLineも使う場合だけ、設定画面の詳細欄にある設定例を手動で反映します。`refreshInterval`はローカルcommandの再実行間隔で、クラウド利用率のpolling時刻ではありません。アプリはユーザーの`.claude/settings*.json`を編集しません。

接続テストまたはstatusLineで取得した値は、アプリのprocess内では設定画面を保存しても維持します。使用率の実値はsettingsへ保存しません。このためアプリ再起動後に「statusLineのみ」を使用している場合は、最初のstatusLineが届くまで「接続済み — 利用情報を待っています」と表示します。接続テスト済みであることと、現在の利用情報を受信済みであることは別々に扱います。

#### LLMによるセットアップ支援（非推奨）

通常は、設定画面「Claude Code連携を設定する」の案内に従って手動で設定してください。手順が難しい場合は、ローカルPCを操作できるLLMへ次のプロンプトを入力して支援を依頼できます。ただし、LLMの挙動や実行権限は製品ごとに異なるため、この方法は推奨・保証しません。APIキー、token、passwordなどの秘密情報はプロンプトへ入力しないでください。

信頼確認はセキュリティ上重要な操作です。LLMに承認させず、表示されたフォルダーが`%LOCALAPPDATA%\CodexUsageMonitor\ClaudeCliWorkspace`であることを利用者自身が確認して承認してください。この旧製品名を含むパスは、既存の信頼承認と設定を引き継ぐための互換識別子として維持しています。

```text
このWindows PCで、AI Usage MonitorがClaude Codeの5時間枠・週間枠を取得できるようにセットアップしてください。

必須条件:
- 作業前にAI Usage MonitorのREADMEと、適用されるAGENTS.mdなどのローカル作業規則を確認する。
- 公式Claude Code CLIの場所とversionを確認し、claude.exeのAuthenticode署名が有効でpublisherが「Anthropic, PBC」であることを確認する。未導入、署名不正、必要flag不足の場合は停止して報告する。
- APIキー、OAuth token、password、browser cookie、認証ファイル、.env、%USERPROFILE%\.claude配下の設定や履歴を読んだり表示したりしない。
- %LOCALAPPDATA%\CodexUsageMonitor\ClaudeCliWorkspace以外のフォルダーをClaude Code自動取得用workspaceとして使わない。ユーザーの.claude/settings*.jsonやAI Usage Monitorのsettings.jsonを直接編集しない。
- Claude CLIの起動には --setting-sources '' --tools '' --no-chrome --strict-mcp-config --safe-mode --ax-screen-reader をすべて使用する。
- 信頼確認を自動操作しない。可視PowerShellでCLIを起動したら作業を停止し、対象フォルダーを表示して、ユーザー本人に内容確認と承認を依頼する。
- ユーザーが承認完了を伝えた後だけ接続テストへ進む。テストでは /usage と終了操作以外をClaude CLIへ送らず、model prompt、dummy request、tool callを実行しない。
- 使用率の実値、account情報、session ID、生のCLI画面をログや回答へ保存・表示しない。
- 最後に、CLIのversion、署名判定、専用フォルダー、5時間枠・週間枠の取得成否、残存した確認用processだけを報告する。

重要:
信頼確認画面では、あなたはキー入力やクリックを代行せず、必ず私の承認を待ってください。追加install、CLI更新、設定ファイル編集、processの強制終了が必要になった場合も、実行前に理由と対象を示して確認してください。
```

承認後は、設定画面の「接続テストを実行」で取得を確認します。成功しない場合は[トラブルシューティング](docs/troubleshooting/README.md)のClaude Code自動取得を確認してください。
# reset時刻と取得日時

Claude `/usage` のreset時刻は、5時間枠・7日間枠それぞれの期間内にある未来時刻だけを受理します。
日付のない過去時刻は原則として翌日へ推測しません。ただし5時間枠のTokyo time-only表記だけは、
翌日候補が5時間＋2分以内に収まる場合に限って受理します。週間枠、範囲外候補、未対応の表記、
東京以外の明示タイムゾーンは誤変換を避けるため取得失敗として扱います。表示中の値には正常取得日時を表示し、
更新失敗または期限切れ時は「最終取得時間」として最後の成功時刻を維持します。
reset拒否時の診断情報はreason codeと、表記category、zone category、候補との差（分）、
window期間だけに限定し、生のCLI画面、使用率、account、絶対日時は保持しません。

Claudeの`CLI`は`/usage`画面の読取り完了時刻、`SL受信`はstatusLineをpipeで受信した時刻です。どちらもAnthropic server上の測定時刻ではありません。正常取得日時は利用枠の行数や補助表示の有無にかかわらずprovider見出し行へ表示し、
CodexとClaudeで同じ水平位置に揃えます。
