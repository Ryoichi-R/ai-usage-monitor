# macOS Claude Code 自動互換性確認の実装・受入記録

確認日: 2026-10-18（Asia/Tokyo）。実機はmacOS 27.0.1（26A434）、arm64。対象はmacOS版Claude active取得。普段のClaude Codeの更新設定や起動リンクを変更せず、版許可リストから実体単位の自動確認へ変更した。

## 実装

- Apple公開Security APIのidentifier・Team ID・digest algorithm・opaque unique bytesを期待identityとして、inspection、version、PTYの各起動へ結合した。公式署名の静的・動的検証は毎回行う。固定版判定とhelp flag照合は廃止した。
- Claude専用v2 protocolで子のexit/signal、監督異常、回収結果、再開有無を分離した。公式署名の新実体への切替と、署名失敗・説明できない不一致を区別する。Codexの旧protocolは維持した。
- 起動前pending guardとstateを耐久化し、最終置換後の保存失敗でも未完了停止を維持する。型不正・未知schema・権限・symlinkを拒否する。安全停止は実体単位、非互換は実体とアプリ/helper/parser契約単位。旧記録の推測移行は行わず、根拠付き保守移行か専用解除を要求する。
- inspectionをbackoffより先に実行し、一時失敗を1/5/15/30/60分で再試行する。手動取得で期限が変わった場合も待機中のpollを起こし、設定画面の予定へ追従する。設定世代・取得方法変更・終了で取消し、回収を待つ。
- 設定に通常の再確認と安全停止の専用解除を追加した。安全停止の理由・時刻・短縮identityと対処文書を表示し、原因調査・回収確認のチェック後だけ解除する。解除履歴は保持する。

## 自動検証

`pwsh -NoProfile -File scripts/test-ai-usage-monitor-macos.ps1 -Coverage`: **758 tests、0 failed、0 skipped**。production/native合計 **93.22%（7,581/8,132行）**、未計測project 0。coverage manifestの143 source hashを検証後のソースと照合した。

| 対象 | 合格数 |
| --- | ---: |
| Platform | 5 |
| Core | 164 |
| Codex | 79 |
| Claude | 62 |
| Claude.Cli | 118 |
| App.UI | 182 |
| Platform.Mac | 21 |
| App.Mac | 7 |
| Claude.Mac | 120 |

stdio/PTY双方の親・helper・双方死亡、離脱、churn、identity差替え、署名・socket境界試験も合格。v2では期待identity不一致時に実行されないこと、停止起動後の公式別実体への差替え、署名要件不一致、子exit 42とhelper正常の分離を確認した。

未知版・prerelease・unknown表記、version非ゼロ終了とcache、段階backoff・新候補の即時再確認、公開直前の更新、非互換・安全停止の維持、旧記録の根拠付き移行と別path、未完了・保存失敗・取消・世代変更・停止解除を合成データで検証した。最終化前・置換後・sync後の失敗注入で、独立guardからpendingが回復することを確認した。別テストホストをpending保存直後と安全停止保存直後に急終了させ、次のプロセスから各記録が残ることも確認した。実利用CLIをクラッシュ試験用に強制終了していない。

`pwsh -NoProfile -File scripts/format.ps1 -Check`、`scripts/lint.ps1 -Mode Fast`、`git diff --check`: PASS。Windows向け全solutionを`EnableWindowsTargeting=true`でReleaseビルドし、Avalonia/WpfLegacyと各testsのコンパイルが警告0・エラー0で成功した。Windows上の実行はMacから行えないため、workspace TODOのCUM-20へ期限付きで記録した。

## 実CLIと導入

現在選択されているCLI **2.1.292**、公式署名の実体で確認した。旧版コピーの追加・優先、起動リンクやユーザー設定の変更は行っていない。既存の信頼済み専用workspaceを使い、trust操作を代行していない。

署名済み導入候補のhelperと、バイト一致を確認した6 production DLLで、**10/10回Available、2枠取得、Clean、Escape後Ready復帰**を確認した。所要時間は9.876〜11.255秒（中央値10.176秒）。生画面・利用値・アカウント情報は保存していない。未知引数の実測も、照合pattern一致・exit 1・Clean・helper正常を確認した。

ローカルarm64自己完結型bundleのad-hoc署名を検証し、manifestの252 fileと導入先を照合した。稼働版を通常終了して残留なしを確認し、旧.appと設定をprivate backupへ保持して差し替えた。起動後もsettingsのSHA-256は変更前と一致した。

再起動後、**12:42:35 JST**にアプリ自身が`claude-status:Available:-`を記録し、`started`へ到達した。`ClaudeCompatibility`は0700、stateは0600、pendingなし・active blockなしを確認した。

通常周期でも12:47:39に次の取得が始まり、**12:47:48 JSTにClean回収**した。最終化後のpending・active blockは0、Availableを維持した。同一availability/reasonはログで重複省略されるため、2回目の起動・Ready復帰・Clean・state最終化と、状態変更エラーがないことを併せて照合した。

## 制約

将来のすべてのClaude Codeを保証するものではなく、実体変更後の取得が成立した場合に自動継続する。未知制御・引数非互換・署名/監督異常を通過させない。D12のmacOS固有の子孫追跡制約は[ADR 010](../adr/010-macos-claude-cli-screen-acquisition.md)に残る。電源断そのものの物理試験とWindows上の実行は未実施。commit・push・外部公開は行っていない。
