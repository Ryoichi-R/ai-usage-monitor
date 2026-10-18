# macOS実機受入記録（2026-09-27）

Mac Studio（Apple Silicon）、macOS 27.0（build 26A428）。対象は未コミットの作業ツリー（HEAD `3c665c5`＋同日の是正）から生成した`artifacts/macos-20260927T125756Z/AI Usage Monitor.app`を`~/Applications`へ複製したもの（arm64、ad-hoc署名）。Claude CLI 2.1.274（native installer）、Codex CLIは`ChatGPT.app`同梱版。項目ごとの手順と結果は[チェックリスト](ai-usage-monitor-macos-acceptance-checklist.md)を正とする。アカウント名、使用率の実値、reset時刻、スクリーンショットは記録しない。

## 結果

| 区分 | 項目 | 結果 |
| --- | --- | --- |
| P0-2再確認（Avalonia 12.1.2、デモモード） | 枠なし透過、常に手前、デスクトップ最背面、クリック透過、メニューバー、複数ディスプレイ・倍率（1-1〜1-6） | OK |
| Phase 3（ウィジェット） | 取得なしでの起動、Dock非表示とメニューバー、設定の保存と再起動後の復元、ディスプレイ位置の復元、二重起動防止、ログイン時の自動起動、層の劣化なし（2-1〜2-7） | OK |
| Phase 4（Codex） | 実アカウントでの表示、終了後の残留なし、取得中の強制終了後の残留なし（3-1〜3-3） | OK |
| Phase 6（Claude active） | 実CLIでの取得、launcherの版が変わらない、繰り返し取得後の終了・取得中の強制終了で残留なし、隔離fileなし（4-0、4-2〜4-6） | OK |
| Phase 6（Claude active） | 未信頼時に「フォルダー信頼が必要」と表示し入力を送らない（4-1） | 保留 |
| Phase 5（Claude passive） | statusLine設定後の`SL受信`、アプリ終了中もstatus lineが止まらない、「statusLine参考値」表示（5-1〜5-3） | OK |

利用者の目視・操作による確認と、Claude Codeによる確認（残留process、診断ログ、launcherの版、LaunchAgentからの起動）を併用した。どちらによる確認かはチェックリストの結果欄に書いた。

## 受入中に見つけて修正した不具合

Claude連携画面が「接続しましたが、利用情報を取得できません」となった。診断ログへ取得状態の理由コードとVTが拒否した制御の種類を記録するようにし、実CLI 2.1.274が起動直後に送る`CSI > 0 q`と`CSI < u`を、製品のVT許可リストが代表形の完全一致でしか受け付けていなかったことを特定した。許可リストをP0-3の取得PoCの分類へ合わせて解消した。詳細は[Claude取得の実装記録](claude-macos-acquisition-2026-09-27.md)の追補を参照。

## 終了・回収の実測

| 場面 | 結果 |
| --- | --- |
| Codex稼働中にquit（Apple Event）×3 | いずれも`residual=0` |
| Codex稼働中に`kill -9` | 最初の確認時点で`residual=0` |
| Claude CLIのPTY取得中に`kill -9` | 3秒後に`residual=0`。後始末用directory 2件は次回起動時のsweepで削除 |

残留の確認は`scripts/check-macos-residual-processes.py`（監督helperのjournal、実行ファイル名、追跡tokenを照合する読み取り専用の確認）で行った。

## 残る制限と未確認

1. **4-1（未信頼時の表示）**: 専用フォルダーが既に信頼済みだったため確認できなかった。fake CLIの自動testでは信頼確認の文言をSetup／`CLAUDE_TRUST_REQUIRED`へ写像し入力を送らないことを確認済みだが、実CLIの信頼確認画面を製品のVT画面モデルへ通したことはない。今回と同様に未対応の制御で画面が無効になると、「信頼が必要」ではなく「利用情報を取得できません」と表示される可能性がある（その場合も入力は送らず、fail-closedは保たれる）。確認には利用者のClaude Code設定（信頼の記録）を変える必要がある。
2. Windows実機での受入（Phase 2の同等性、BAT、Windowsのmanaged settings検査、配布物への`licenses/`同梱）は未実施（計画D15、TODO CUM-18・CUM-19）。
3. Claude active取得は検証済みの2.1.274に限る。版が変わったら計画の再検証手順を行う。
