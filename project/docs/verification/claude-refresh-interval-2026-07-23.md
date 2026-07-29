# Claude Code refreshInterval verification receipt

- Date: 2026-07-23 (Asia/Tokyo)
- Result: black-box probe blocked; `refreshInterval` remains 30

## Confirmed

2026-07-23に公式Claude Code status line文書を再確認した。`refreshInterval`はイベント駆動更新に追加してcommandをN秒ごとに再実行し、最小値は1である。status lineはlocalで実行され、API tokenを消費しない。

## Not confirmed

ユーザーのsettings、認証情報、既存statusLineを読まず変更せず、かつnetwork promptを発生させないisolated interactive sessionを自動構築できなかった。このためidle中の外部PowerShell process実起動間隔は確認済みと扱わない。

計画の分岐条件に従い、300秒化、spawn削減効果、最大約5分の再送待ちを実装前提にしない。
