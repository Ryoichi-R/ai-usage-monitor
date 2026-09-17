# Troubleshooting

## Codex

- `CODEX_NOT_FOUND`: 設定でnative `codex.exe`またはnpm shimの場所を指定してください。
- `SIGNED_OUT`: Codex CLIまたはDesktopでChatGPTアカウントへログインしてください。
- `RPC_TIMEOUT`: ネットワークとCodexの動作を確認し、「今すぐ更新」を実行してください。
- `METHOD_UNSUPPORTED`: Codexを更新してください。認証情報や非公開endpointへfallbackしません。
- `SCHEMA_UNSUPPORTED`: App Serverは応答しましたが、使用上限windowを解釈できません。金額componentが正常ならそのカードだけ表示される場合があります。
- `CODEX_HOME_UNAVAILABLE`: 追加アカウントの `CODEX_HOME` が絶対パスでないか、存在しません。既定アカウントへfallbackしません。
- `CODEX_HOME_DUPLICATE`: 別の設定行と同じ実効 `CODEX_HOME` です。設定順で先の行だけを監視します。
- `DUPLICATE_ACCOUNT`: 別の `CODEX_HOME` が同じChatGPTアカウントへサインインしています。公式Codex CLIで後続homeを意図した別アカウントへサインインし直してから「今すぐ更新」を実行してください。手動更新時だけ停止中homeを1回ずつ再確認します。

## Claude Code 自動取得

既定のAutomaticは毎周期と「今すぐ更新」で公式CLIの`/usage`を試行します。HUDの`CLI`は画面の読取り完了時刻で、server measurement timestampではありません。statusLineへfallbackした場合は「statusLine参考値 — 最新性未保証」と表示します。`SL受信`はbridgeのpipe受信時刻であり、これもserver measurement timestampではありません。

通常運用は既定300秒を推奨します。60秒は診断・高頻度確認向けで、±10% jitterの下限54秒に対してactive処理が長い環境ではほぼ連続起動になり得ます。900秒設定ではactive TTLが33分となるため、障害時に最後のCLI値を最大33分表示する場合があります。

手動セットアップが難しい場合は、通常手順を優先したうえで、READMEの[LLMによるセットアップ支援（非推奨）](../../README.md#llmによるセットアップ支援非推奨)にある制限付きプロンプトを利用できます。信頼確認はLLMに代行させず、利用者自身で対象フォルダーを確認してください。

- `CLAUDE_NOT_INSTALLED`: 公式Claude Code native installerを導入するか、設定で`claude.exe`を指定してください。
- `UNTRUSTED_EXECUTABLE`: 実行ファイルのAuthenticode署名が無効、またはpublisherが`Anthropic, PBC`ではありません。自動実行しません。
- `REQUIRED_FLAG_MISSING`: CLIを更新してください。必要flagがないversionをblindly起動しません。
- `CLAUDE_TRUST_REQUIRED`: 通知領域の「Claude Code連携…」を開き、表示された専用フォルダーで利用者自身が公式CLIのtrustを承認してください。監視アプリは選択を代行しません。
- `CLAUDE_SIGNED_OUT`: 公式Claude CLIへ先にサインインしてください。
- `READY_TIMEOUT` / `USAGE_SCREEN_PARSE_FAILED`: offline、managed settings、CLI更新による画面変更を確認し、手動更新を1回試してください。段階描画中のgenericな不完全画面は起動timeout内で再読込します。完成後もparserが5時間枠・週間枠の両方を一意に確認できない場合、値を推測しません。
- `CONSOLE_BUFFER_READ_FAILED`: consoleへのattach、画面情報取得、または画面読取りが起動timeoutまで継続して失敗しています。アプリを再起動し、改善しない場合はmonitorとClaude CLIのelevation level、endpoint security policy、Claude CLIの更新状況を確認してください。
- `ATTACH_FAILED` / `SCREEN_READ_FAILED`: monitorとClaude CLIのelevation levelを揃えてください。
- `PROCESS_START_FAILED`: 実行file、アクセス権、endpoint security policyを確認してください。

managed settingsはCLI引数で無効化できません。managed customizationの影響で既知画面契約を満たさない環境はactive sourceの保証対象外で、値を推測せず取得不能として扱います。

## passive statusLine

常駐CLIからも受信する場合は、設定画面の詳細欄にあるstatusLine例を設定生成元へ手動で反映します。アプリは`.claude/settings*.json`を編集しません。既存statusLineがある場合は置換せず、stdinをbufferしてbridgeと既存処理へ複製し、既存処理のstdoutだけを表示に使ってください。

接続テスト完了後やアプリ再起動直後に「接続済み — 利用情報を待っています」と表示される場合は、連携設定は完了していますが、このprocessではまだ最初のstatusLineを受信していません。Claude Code sessionからstatusLineが届くと利用情報表示へ移ります。「statusLineのみ」は通常refreshでCLIを起動しません。

接続テストで利用情報を取得できた後、同じprocess内で設定画面を保存しただけで「未接続」へ戻るのは正常な待機状態ではありません。修正版では設定保存時に既存のcoordinatorと観測状態を維持します。再現する場合は、起動中のEXEが修正版の配置先であることを確認してください。usage実値、生のCLI画面、account情報、`.claude`設定全体を診断ログとして収集しないでください。

## STALE / ERROR

activeは`max(10分, 更新間隔×2.2)`、passiveはpipe受信から10分を超えた場合、またはreset時刻を過ぎた場合にstaleとします。TTL内のrefresh失敗では前回値を表示しつつ警告します。malformed JSON、protocol不一致、0～100外のpercentage、観測時刻と同じか過去のreset、または対象window期間＋2分を超えるresetは拒否します。
# Claudeの使用率が消えた／最終取得時間が古い

曖昧なreset表記、未対応のタイムゾーン、またはreset通過後に新しい有効値を取得できない
場合、古い割合を現在値として残さず非表示にします。「最終取得時間」は最後に全windowを正常
取得した時刻です。手動更新で有効な値が返れば自動復帰します。通常pollingが動作している
場合の条件付き回復上限は、次回jitter上限330秒とsource timeout 30秒の合計です。
CLIが有効表記へ戻るまでの時間には、アプリ単独で保証できる絶対上限はありません。
reset固有reasonは曖昧な過去時刻、window範囲外、未対応zone、未対応形式を区別します。
5時間枠のTokyo time-onlyが当日中では過去でも、翌日候補が5時間＋2分以内なら
cross-midnight resetとして受理します。週間枠や範囲外候補は翌日へ送りません。
診断contextには表記・zoneのcategory、候補との差（分）、window期間だけを使用し、
生の画面行、使用率、account、絶対日時は記録しません。

## 外観・背景

- 背景を変更しても文字まで薄くなる場合は、「背景の不透明度」と「全体の不透明度」を確認してください。前者は背景だけ、後者は従来どおりウィジェット全体へ適用されます。
- 白など明るい背景で文字が読みにくい場合は、外観タブの通常文字色・補助文字色・アクセント色を暗い色へ調整してください。
- 「他のウィンドウと重なる部分では背景だけ隠す」は、背景ON、背景不透明度が0より大きい、常に手前ONの条件でSplit背景になります。常に手前をOFFにすると設定値は保持したままInline背景へ戻ります。
- Split背景がWindowsのZ-order競合やExplorer再起動で維持できない場合は、情報表示を残して背景だけを一時的に透明化します。次の表示・タイマー・タスクバー／ディスプレイイベントで再試行します。
- 外観設定の保存に失敗した場合は、保存前の色・背景・位置を維持します。再度保存する前に、設定ファイルを手動編集したり認証情報を診断ログへコピーしたりしないでください。

## Claude CLIのセッションリセット時刻がない場合

Claude Code 2.1.274では、5時間枠が0%の画面でリセット時刻の行が省略される場合があります。修正版は、0%だけのセッション欄に続く週間枠を正常に解析できた場合、この表示を受理します。存在しないリセット時刻は推測せず、RESET — と表示します。0%以外での時刻欠落、未知の行、不正なリセット表記、週間枠の不完全な表示は引き続き取得エラーとして扱います。
