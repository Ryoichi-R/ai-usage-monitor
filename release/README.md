# Release and artifact policy

このディレクトリは、AI Usage Monitorの公開判定と成果物の扱いを定義します。バイナリ本体はここへ保存しません。

## 3つのプロファイル

- `source-repository`: source、tests、scripts、文書のみ。`project/artifacts/`の生成物は候補から除外します。
- `binary-local-only`: 内部QA専用の候補です。`internal-test`と`readiness-blocked`を明示し、公開アップロード対象にはしません。
- `binary-release`: exact candidate hashに結び付いた公開用Release assetです。必須検証とSHA-256記録が完了するまで選択しません。

## payload

利用量取得のraw payloadはprocess内だけで扱い、ディスク、source、Release assetへ保存しません。テストで必要なデータは匿名化fixtureに限定します。

自己完結型のx64／ARM64バイナリは、ビルド中は`project/artifacts/`に生成します。このパスはsource管理対象外です。公開する場合だけ、candidateのSHA-256、runtime、検証記録、同梱文書をmanifestへ記録し、外部のRelease assetとして登録します。

## readiness

`binary-release`では、公開する候補そのものを対象に実機受入を行います。x64はx64 Windows、ARM64はARM64 Windowsで起動、表示、主要provider接続、設定保存、終了を確認します。過去の候補での受入結果は、異なるハッシュの候補へ自動的には継承しません。

readinessがBLOCKEDの候補は`binary-local-only`に限って保持できます。ファイル名または格納先に`internal-test`と`readiness-blocked`を含め、Release assetや利用者向け導入手順から分離してください。

## root / project boundary

ルートには利用者向けREADME、LICENSE、SECURITY、CONTRIBUTING、第三者通知、開始BATを残します。実装、テスト、詳細文書は`project/`に置きます。`project/LICENSE`は配布物同梱用のコピーであり、ルートの正本と一致させます。
