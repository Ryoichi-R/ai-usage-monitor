# 他PCでの開発と検証

この手順は開発中のソースを別PCで取得して検証するためのものです。完成版バイナリの導入手順ではありません。各PCで同じcommitを確認し、そのPCのOS・CPU向けにビルドします。

## 対象と準備

- Windows 11 Intel/AMD: win-x64。
- Windows 11 ARM64: win-arm64。x64エミュレーションでの成功をARM64の受入へ流用しません。
- Apple Silicon Mac: osx-arm64。Intel Mac/Linux ARM向けの手順は未整備です。
- Git、project/global.jsonに適合する.NET 10 SDK、PowerShell 7.4以上を用意します。
- MacのテストにはPython 3とXcode Command Line Toolsも必要です。
- Codex/Claudeの公式アプリ・CLIの導入とログインはPCごとに行います。認証情報やWindowsの絶対パスを含むフック設定は別PCへコピーしません。

既存cloneがある場合は、実際のGit root・remote・未公開変更・稼働中アプリを先に確認します。新しいsourceを既存の変更へ上書きせず、必要な退避と切替を利用者が判断します。

## 新しいフォルダーへ取得

```powershell
git clone https://github.com/Ryoichi-R/ai-usage-monitor.git
cd ai-usage-monitor/project
```

Mac用とWindows ARM64用の修正は別ブランチで進め、検証結果にOS、CPU、commit、実行物のhashを記録します。

## テストとビルド

全OS共通のテスト入口です。テストはfake CLIを使い、実アカウントの接続確認とは分けます。

```powershell
pwsh -NoProfile -File scripts/test-ai-usage-monitor.ps1 -Coverage
```

Windowsでは対象に合う一方を選びます。

```powershell
pwsh -NoProfile -File scripts/publish-ai-usage-monitor.ps1 -Runtime win-x64
pwsh -NoProfile -File scripts/publish-ai-usage-monitor.ps1 -Runtime win-arm64
```

出力はproject/artifacts/win-x64またはwin-arm64です。既に利用中の版を更新するときは、READMEの通常終了・backup付きrebuild手順に従います。

稼働中の版を保持したままARM64候補を検証するときは、新規のstaging出力先を指定します。leaf名はスクリプトが許可する小文字32桁GUIDの形式に従います。

```powershell
$arm64Output = "artifacts/.staging-ai-usage-monitor-win-arm64-" + [guid]::NewGuid().ToString("N")
pwsh -NoProfile -File scripts/publish-ai-usage-monitor.ps1 -Runtime win-arm64 -OutputDir $arm64Output
```

通常版へ切り替える際は、稼働中の版を通常終了し、配布物・設定・既存state・変更対象hookをバックアップします。新しい候補の設定や合成試験の履歴で通常profileを上書きせず、採用する候補のhashと接続先を確認します。自動起動の登録先が変わる移動やproject hookの変更は、切替内容を利用者と確認してから実施します。Codexの更新後hookは本人のReview hooksが必要です。

MacではMac上でビルドします。

```powershell
dotnet build AiUsageMonitor.Mac.slnx
pwsh -NoProfile -File scripts/publish-ai-usage-monitor-macos.ps1
```

出力はproject/artifacts/macos-*内のAI Usage Monitor.appです。ad-hoc署名付きの開発版であり、署名検証の成功をGatekeeperや実CLI・画面の受入結果へ置き換えません。 Developer IDとHardened Runtimeの準備、証明書がない場合の制約は[Macの署名・公証準備](macos-signing.md)を参照してください。

## 作業状態の接続

アプリの設定で作業状態を表示し、「作業状態の接続設定…」からそのPC・project用の例を生成します。既存の権限・statusLine・フックを保持して適用し、Codexでは本人がReview hooks/設定→Hooksで内容を確認して有効にします。接続例の生成だけでは受信確認にはなりません。詳細は[作業状態監視](work-status.md)を参照してください。

## 実機で確認する項目

- 利用枠の取得、取得日時、表示位置、通常/コンパクト表示。
- 作業中のsession数、入力待ち、質問への回答後の解除、応答終了。
- CodexとClaudeそれぞれからの実イベント受信。試験のためのメッセージ送信は利用者の許可を得ます。
- 設定保存、トレイ/メニューバー終了、再起動、既存状態・確認履歴の保持。
- ARM64では「バージョン情報」の実行アーキテクチャと、外部CLIを含む対象環境。

未実行項目を成功扱いせず、既存の状態ファイルを削除して検証を通しません。対象候補そのものの実機受入が終わるまでは、[公開方針](../../release/README.md)のbinary-local-onlyとして扱います。

## 検証記録と候補の対応

自動テスト、合成イベントの表示、実Desktopからの受信、利用枠の実取得、本人による画面確認を別々に記録します。OS/CPU、OSとSDK・CLI・アプリそれぞれの実行アーキテクチャ、branch/commitと未コミット変更、候補とBridgeのSHA-256、実行コマンド、テスト件数・coverage・失敗/skip、確認済み/未実施の項目を含めます。詳細はGit追跡対象外の`project/.work/`等へ保存し、認証情報・会話/応答本文・tool入力・利用枠の生応答を記録しません。

複数候補で試験した場合は項目ごとに候補を対応付け、過去の結果を異なるhashへ自動継承しません。通常版への切替後は、同じ通常profileでの利用枠・設定保持・実フック受信を確認し、隔離版の結果とは区別します。現在のWindows ARM64の確認範囲と未実施項目は[作業状態の検証状況](work-status.md#対応状況と検証)を参照してください。
