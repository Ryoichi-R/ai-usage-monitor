# Claude Code 2.1.296 macOS再検証

実施日: 2026-10-12（Asia/Tokyo）。同日19:12にClaude Codeが2.1.296へ自動更新され、19:15から`CLI_VERSION_REVALIDATION_REQUIRED`でactive取得が停止していた。利用者の依頼に基づき対象リポジトリで再検証・修正し、インストール済みアプリへ反映した。

## 変更

- Macの検証済みCLIを2.1.285から2.1.296へ更新した。旧版・未知の版は引き続き拒否する。
- 2.1.296は起動時にProgram Status Protocolの対応を問い合わせる`OSC 7501 ; ?`を1回送る（CLI同梱の変更履歴と、応答がなければ以後送らない実装を確認）。製品VTは`OSC 0`と`OSC 133`以外を拒否していたため、版の更新だけでは全取得が失敗していた。問い合わせの形（payloadが`?`だけ）に限って受理し、状態通知など他の7501は従来どおり拒否する。応答は返さない。
- 監督helper（`process-supervisor.c`）とprobeの子孫登録で、snapshot後・`getsid`前に子が終了すると`getsid`がESRCHで-1を返し、group内の子をsession離脱と誤判定していた。終了済みと再確認できた場合だけsid比較を省き、生存中の失敗はfail-closedのまま離脱として扱う。製品で誤判定すると`CLI_GROUP_ESCAPE_DETECTED`で2.1.296が隔離されるため、同時に修正した。
- 画面probeに、OSCのpayloadが`?`だけか（query / data）を数える分類を追加した。payload自体は記録しない。
- native受入に、短命な子を2,000回起動する`churn`ケースを追加した（stdio / PTY）。

## 実CLI

既に利用者が信頼したprobe専用の空workspaceを使用し、trust選択は操作していない。認証情報や設定本文は読み出していない。利用率、アカウント、未匿名化画面、画面の生バイトは保存・表示していない。

- managed settingsの既知pathとremote settingsのmetadata: 不在。既存quarantine: 不在。
- Apple anchor、identifier、Developer ID chain、Team IDによる静的署名確認: PASS（`codesign --verify --strict`もPASS）。
- 監督付き`--version`と`--help`: 停止起動、動的署名とunique identityの一致、2.1.296と隔離オプション7種を確認。離脱・残留ゼロ。
- 署名・子孫追跡付きPTYで`/usage`を計24回観測（初回10回、OSC分類追加後2回、計測用一時build 12回）: 24/24でReady、usage、Escape後のReady復帰を確認。未対応VT 0、残留0、fixture書き出し0。
- 初回10回のうち1回で、短命な子`uname`がpgid不変・sidのみ変化の「離脱」と判定された。pgid不変のままsessionだけが変わることはなく、macOSの`getsid`はESRCHでしか失敗しないため、上記の登録時競合による誤判定と判断した。errnoを記録する一時buildの12回では再発しなかった（偶発的で、errnoの実測はできていない）。
- 引数付きCSIは25種。2.1.285からの差分は`17A`/`17B`/`21A`/`21B`の追加と`19A`/`19B`の不出現。OSCは`0`（data）、`133`（data）、`7501`（query）。

## 自動検証

- Mac全suite: 711件PASS（前回706件＋今回追加5件）。
- native受入: stdio / PTYの異常終了、group離脱、churn、identity不一致、socket境界をすべてPASS。
- production coverage: 94.29%（7,123 / 7,554行）、未計測0、90% gate PASS。
- format checkとFast lint: PASS。
- arm64自己完結型.appの生成とad-hoc署名検証: PASS。
- 修正前のhelperに対するchurnケースは、`true`連続起動・`sh -c uname`連続起動とも再現しなかった。churnケースは競合を確定的に再現する回帰testではなく、短命な子を離脱と扱わないことの確認にとどまる。

## 反映後の確認

- 稼働中の.appを作業バックアップ（`artifacts/backup-20261012-before-cli2.1.296/`、260ファイルのhash一致）へ`ditto`で保存してから終了し、更新版を配置した。配置後に260ファイルのhashとad-hoc署名を確認した。
- 20:32:13に`claude-status:Available:-`と`started`を診断ログで確認した。quarantineは作成されず、Claude CLIの残留processもない。
- commit、push、公開は実行していない。Windows実機では未実施（共通部の変更はない）。任意の将来版を許可する変更はしていないため、次のCLI版変更時にも再検証が必要。
