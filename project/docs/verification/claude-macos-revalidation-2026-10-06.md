# Claude Code 2.1.285 macOS再検証

実施日: 2026-10-06（Asia/Tokyo）。ワークスペース内の更新候補で実施した。利用者の明示承認後、同日00:29に対象リポジトリとインストール済みアプリへ反映した。

## 変更

- Macの検証済みCLIを2.1.274から2.1.285へ更新した。旧版・未知の版は引き続き拒否する。
- `ClaudeCliActiveSource`が失敗時に取得元を`StatusLineActive`としていたため、state storeが具体的な理由を`ACTIVE_SOURCE_NOT_CLI_SCREEN`へ上書きしていた。失敗時も`CliScreen`として返し、版変更・group離脱・未認証・trust要求の理由を保持する。
- 実測したCSI制御23種を、数値引数を含む形でVTモデルへ再生する回帰テストを追加した。
- 署名付きfake CLIの日付生成を`%e`から`%d`へ変更した。一桁の日に月と日の間が二重スペースになる既存fixtureの問題を修正し、製品parserの許可範囲は変更していない。
- 再検証用probeにも`DISABLE_AUTOUPDATER=1`を渡し、画面probeで数値引数を含むCSIの種類だけを記録する。

## 実CLI

既に利用者が信頼したモニター専用の空workspaceを使用し、trust選択は操作していない。認証情報や設定本文は読み出していない。利用率、アカウント、未匿名化画面、画面の生バイトは保存・表示していない。

- managed settingsの既知pathとremote settingsのmetadata: 不在。既存quarantine: 不在。
- Apple anchor、identifier、Developer ID chain、Team IDによる静的署名確認: PASS。
- 監督付き`--version`と`--help`: 停止起動、動的署名とunique identityの一致、2.1.285と隔離オプション7種を確認。離脱・残留ゼロ。
- 署名・子孫追跡付きPTYで`/usage`を10回観測: 10/10成功。すべてReady、usage、Escape後のReady復帰を確認。離脱0、残留0、未対応VT0、fixture書き出し0。
- 更新候補の能力判定・製品PTY・VT・共通parserによる実取得: Available、2枠、理由なし、終了後journalゼロ、quarantineなし。
- 実測CSI全23種を製品VTへ再生: 全件受理。既存の匿名化fixtureの分類・parserテストもPASS。新たな実画面fixtureは保存していない。

## 自動検証

- Mac全suite: 705件PASS。その後に追加した実測CSI回帰テストを含むMac取得系80件もPASS（検証した一意のテスト合計706件）。
- 既存native受入: stdio/PTYの異常終了、group離脱、identity不一致、socket境界をすべてPASS。
- production coverage: 94.27%（7,120 / 7,553行）、未計測0、90% gate PASS。
- 既存format checkとFast lint: PASS。
- arm64自己完結型.appの生成とad-hoc署名検証: PASS。

Windows実機では未実施。共通取得元の変更はMac上の共通テストで検証した。UI操作・インストール後の取得確認は反映後に行う。任意の将来版を許可する変更はしていないため、次のCLI版変更時にも再検証が必要。


## 反映後の確認（2026-10-06）

- 利用者が指定リポジトリの関連13ファイルとインストール済み.appへの反映・再起動を明示承認した。
- 変更前のソースをhashで再照合し、変更対象のみ反映した。元ソースと旧.appをローカルの作業バックアップへ保存した。
- `.app`は署名に必要な拡張属性を保持するmacOSの`ditto`で複製し、配置前後に260ファイルのhashとad-hoc署名を確認した。
- 旧プロセスの終了を確認してから更新版を起動した。診断ログで00:29:44に`claude-status:Available:-`と`started`を確認した。
- 対応版更新と停止理由の保持を稼働アプリへ反映し、取得復旧を確認した。設定・認証・CLI本体は変更していない。
- commit、push、公開は実行していない。Windows実機での確認は未実施。
