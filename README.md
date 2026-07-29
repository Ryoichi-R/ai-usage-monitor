# AI Usage Monitor

OpenAIおよびAnthropicの公式CLIと連携する、Windows用のデスクトップ利用状況ウィジェットです。CodexとClaude Codeの利用枠、リセット時刻、最終取得日時をまとめて監視します。

## 概要

- 実装、詳細な使い方、開発者向け手順: [project/README.md](project/README.md)
- プライバシー: [project/PRIVACY.md](project/PRIVACY.md)
- セキュリティ報告: [project/SECURITY.md](project/SECURITY.md)
- 第三者コンポーネントの通知: [project/THIRD-PARTY-NOTICES.md](project/THIRD-PARTY-NOTICES.md)

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
