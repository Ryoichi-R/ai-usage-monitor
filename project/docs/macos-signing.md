# Macの署名・公証準備

通常のpublishは従来どおりad-hoc署名のローカル開発版を作る。稼働中の通常版を再署名せず、検証済みの配布物を新規の隔離directoryへコピーして準備する。

## 現時点の区別

- ローカル通常版の起動・利用枠・作業状態・保存・正常終了は実機受入済み。
- Developer ID Application証明書は未用意。Developer ID署名、公証、Gatekeeperの配布受入は未実施。
- `notarytool`と`stapler`はXcode Command Line Toolsから利用可能で、新規tool導入は不要だった。
- Hardened Runtimeのad-hoc候補は署名構造の検査専用。2026-11-05の実行試験では、Team IDを持たないため.NETのlibhostfxr.dylib読み込みがlibrary validationで拒否された。通常版へ導入しない。
- この結果をDeveloper ID署名版の成功や失敗へ流用しない。同一Teamの証明書で本体・Bridge・native libraryを署名した候補の実行受入が別途必要。

## ローカルでの準備

`project`から実行する。sourceは既存publishの`.app`で、同じ親directoryの`bundle-manifest.json`との全file hash一致と署名を検証する。source/outputともproject/artifacts配下だけを許可する。symbolic link、manifestの重複/外部path、既存outputへの上書きを拒否し、入力と通常版を保持する。

```powershell
pwsh -NoProfile -File scripts/prepare-ai-usage-monitor-macos-signing.ps1 `
  -SourceBundle '<既存publish先>/AI Usage Monitor.app' `
  -OutputRoot '<project/artifacts内の新規directory>' -WhatIf
```

`-WhatIf`を外すと既定の`HardenedAdHoc`検査候補を作る。これは実行/導入用ではない。出力先には`internal-test`と`readiness-blocked`を含める。

署名対象のMach-Oを内側から処理し、Hardened Runtimeを有効にする。 `Contents/MacOS`はcode directoryのため、managed DLL/dataも選択したidentityで署名し直すが、それらにはruntime flagやentitlementを付けない。.NET apphost3個だけに`com.apple.security.cs.allow-jit`を付与し、native helperやdylibには付与しない。library validation無効化、unsigned executable memory、debug権限は追加しない。

.NETのJIT権限の根拠: [MicrosoftのmacOS配布手順](https://learn.microsoft.com/en-us/dotnet/core/deploying/macos)。library validationはApple署名か同一Team IDを要求する: [Appleの説明](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.security.cs.disable-library-validation)。

## 証明書を用意した後

Apple Developer Program契約・証明書の発行/設定・ログインは本人が行う。パスワード、秘密鍵、Appleの認証tokenをrepoや検証記録へ置かない。準備scriptはKeychainを列挙しない。

本人が選択した証明書の公開SHA-1 fingerprintを`-SigningIdentity`へ明示し、`-SigningMode DeveloperId`で新規候補を作る。証明書SHA-1は署名対象の識別に使う値で、認証tokenではない。scriptはsecure timestampを要求し、Developer ID Applicationの種別とApple anchorを検証する。

このDeveloperId分岐は現環境では証明書がないため未実行。署名検証成功だけでruntimeやGatekeeperをPASSにしない。`signing-receipt.json`のruntime_acceptance、notarization、gatekeeperは未受入のまま出力する。

## 公証へ進む前の受入

1. 新規の`--work-status-test-root`でGUI、.NET/native library、メニューバー、保存、正常終了を確認する。
2. 発信元確認と公式CLI署名確認を有効にしたまま、同梱Bridge/helperと実利用枠を検証する。実AI送信には内容・件数の許可を得る。
3. 認証は本人がnotarytoolのKeychain profile等で準備する。準備scriptからprofile/認証情報を読み出さない。
4. exact candidate hash、内容、送信先Apple Notary Serviceを示し、公証アップロードの個別許可を得る。Release公開の許可とは別。
5. 公証結果のAcceptedと診断を確認し、`stapler staple`/`validate`を実施する。ZIP配布ならticketを添付した.appを再梱包する。
6. 最終成果物のSHA-256を再記録し、Gatekeeperを有効にしたダウンロード経由の初回起動と、オフラインでのticket検証を確認する。

公証の根拠: [AppleのDeveloper ID案内](https://developer.apple.com/developer-id/)と[公証要件](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution)。OSのsecurity設定を変更して通さない。

## 自動検証

```powershell
pwsh -NoProfile -File scripts/test-macos-signing-contract.ps1
```

小さなARM64 native fixtureを使い、path/hash/symlink/上書き拒否、WhatIf、nativeごとのruntime flag、JIT権限の付与対象、入力保持、未受入markerを検証する。fixtureは検証後も残す。Macの通常test入口からも実行する。

この契約テストの成功は.NET実行受入ではない。HardenedAdHocで実.NET apphost3個がlibrary validationに拒否された結果も、追跡対象外のローカル検証記録へ保持する。
