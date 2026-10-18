# 製品バージョンとビルド情報

AI Usage Monitorの製品バージョンの正本は`Directory.Build.props`の`Version`です。他のアプリやworkspace共通VERSIONには連動しません。今回のバージョン情報機能は機能追加のため、0.1.0から0.2.0へ更新しました。

- 機能追加はminor、互換性を維持する修正はpatch、互換性を壊す変更はmajorを更新します。
- 0.xは安定版前の期間ですが、minorに破壊的変更を混在させず、破壊的変更は1.0.0以上へのmajor更新とします。1.0.0への移行は安定版としての判断でも行えます。
- 同じリリースのWindows x64・Windows ARM64・macOS ARM64・旧WPF版は同一番号です。再ビルド、別OS／アーキテクチャへのビルドでは番号を更新しません。
- 実行ファイルのProductVersion／InformationalVersion、macOSのCFBundleVersion／CFBundleShortVersionString、配布するbuild-info.json、設定のバージョンは同じ値です。.NETのAssemblyVersion／FileVersionだけは4桁形式（例: 0.2.0.0）になります。

## ビルドと表示

`dotnet publish --no-build`では既に埋め込んだビルド日時を保持します。通常の`dotnet build`／`dotnet publish`で、Coreのビルド直前にGit短縮ID（12桁）・作業ツリー全体の未コミット変更の有無・ビルド日時UTCを生成し、JSONをCoreへ埋め込みます。同じJSONを出力／配布へコピーします。trackedの変更と未追跡ファイルを含み、ignoredのbin／obj／artifactsは含めません。Git不在、Git情報未取得、HEAD未作成の場合はソース識別子と変更有無を「不明」にします。Gitの導入は不要です。

画面とコピーは埋め込み情報から同じ順序で生成し、アプリの起動時刻・ファイル更新日時・リポジトリの再読込で置き換えません。OS名と実行中プロセスのアーキテクチャは.NETのOperatingSystem／RuntimeInformation.ProcessArchitectureから表示するため、エミュレーション中もアプリのアーキテクチャを示します。日時はUTCを明記し、端末の言語やタイムゾーンで変えません。ユーザー名、メール、リモートURL、絶対パス、認証情報は取得・埋め込みしません。

設定の「バージョン情報」→「情報をコピー」で複数PCの情報を比較してください。版が同じでも、ソース識別子・変更有無・ビルド日時・実行版で違いを確認できます。変更有無が「あり」の場合、同じIDでも未コミットの内容が異なる可能性があります。未コミット内容の一致を保証するハッシュではありません。

macOSは`scripts/publish-ai-usage-monitor-macos.ps1`、Windowsは`scripts/publish-ai-usage-monitor.ps1`（または既存rebuild入口）を使用します。macOSのInfo.plistとbundle-manifest.jsonはpublishで生成したJSONから版を取得します。既存の配布手順と実機受入要件は変更しません。今回の機能はオンラインの最新版判定や自動更新を行いません。
