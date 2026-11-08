# AI Usage Monitor

> **開発中（Experimental）** — このリポジトリは開発中のソースコードを公開しています。対応OS・CPUごとの実機検証は完了していません。

## 開発・検証状況

| 対象 | 状況 |
| --- | --- |
| Windows 11 x64 | 利用枠表示と作業状態の一部機能を実機確認済み |
| Windows 11 ARM64 | 2026-11-05の開発候補でネイティブ起動・利用枠・作業状態・通常版切替を実機確認。範囲と制約は[検証状況](project/docs/work-status.md#対応状況と検証)を参照 |
| macOS / Apple Silicon | 開発版の実装・ビルド手順あり。実機での受入は未完了 |
| Intel Mac / Linux ARM | 現在の配布・検証対象外 |

作業状態は「作業中 数字」「入力待ち」「応答終了」を表示します。Codexの質問カードと回答後の解除はWindows x64で確認済みです。Windows ARM64では開発候補のCodex/Claude Code実フック受信、入力待ちと回答後の解除、応答終了を確認しています。応答終了は作業の成功や閲覧済みを示すものではありません。

この公開はソースコードの共有を目的とし、完成版の実行ファイルは配布しません。既知の制約は[作業状態の説明](project/docs/work-status.md)、各PCでの開発・接続・検証は[他PC向け手順](project/docs/cross-platform-development.md)を参照してください。

OpenAIおよびAnthropicの公式CLIと連携する、Windows向けのデスクトップ利用状況ウィジェットです（macOS版は開発中）。CodexとClaude Codeの利用枠、リセット時刻、最終取得日時をまとめて監視します。

## 概要

- 実装、詳細な使い方、開発者向け手順: [project/README.md](project/README.md)
- プライバシー: [project/PRIVACY.md](project/PRIVACY.md)
- セキュリティ報告: [project/SECURITY.md](project/SECURITY.md)
- 第三者コンポーネントの通知: [project/THIRD-PARTY-NOTICES.md](project/THIRD-PARTY-NOTICES.md)
- 公開候補と成果物の扱い: [release/README.md](release/README.md)

## 動作要件

- Windows 11
- PowerShell 7
- .NET 10 SDK（`project/global.json` に従う）
- Codex監視を使う場合は、Codex CLIまたはCodex Desktopとログイン済みのChatGPTアカウント
- Claude Code監視を使う場合は、公式Claude Code CLIとログイン済みのAnthropicアカウント

## インストール

rootの「ここから開始 - AI Usage Monitorを導入・更新.bat」を使用してください。利用者向けのビルドと更新は、`project`配下の手順を使用します。

## 使用方法

起動後、タスクトレイのウィジェットからCodexまたはClaude Codeの利用状況を確認します。プロバイダーの認証やCLIの設定は、各公式CLIの手順に従ってください。実装の詳細と開発者向け検証手順は [project/README.md](project/README.md) を参照してください。

## 設定

監視対象と更新間隔はアプリの設定画面から変更できます。認証情報そのものは本プロジェクトへ入力・保存せず、各公式CLIが提供するログイン状態を利用してください。セキュリティ上の設計は [SECURITY.md](SECURITY.md) と [project/SECURITY.md](project/SECURITY.md) に記載しています。

## アンインストール

アプリを終了し、展開した `ai-usage-monitor` フォルダーを削除してください。公式CLIやそのログイン状態は各公式CLIの手順で個別に管理してください。

## 既知の制限

利用枠やリセット時刻は各providerの応答に基づく表示です。provider側の仕様変更、CLIの未インストール、未ログイン、未知の画面状態では値を取得できないことがあります。本ソフトウェアは請求・利用制限・provider側の公式記録を置き換えません。

## ライセンス

AI Usage Monitorの第一者コードはMIT Licenseで提供します。著作権表示は[LICENSE](LICENSE)を確認してください。第三者コンポーネントには各コンポーネント固有のライセンスが適用され、通知は[project/THIRD-PARTY-NOTICES.md](project/THIRD-PARTY-NOTICES.md)に保持します。

## 作業状態監視

利用枠とは別に、Codex Desktop・Claude Codeの作業状態を表示する段階提供の機能を追加している。既定OFFで、project単位の公式hooksが必要。Claude Codeの確定完了は接続時に他のStopフックがないことを確認したprojectに限って判定するが、実機受入は未完了。Macの配布受入と統合版の実機回帰も未完了。[詳細](project/docs/work-status.md)。
