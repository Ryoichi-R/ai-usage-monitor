# Codex monetary usage 自動検証記録

- 実施日: 2026-07-26 (Asia/Tokyo)
- 対象: stable `account/rateLimits/read` の `credits` / `individualLimit`
- 確認: `decimal` strict parse、同一bucket選択、欠落component独立、既存window回帰、WPFカード、成功後のtransport失敗で現在値を全消去
- 公式schema照合: Codex CLI 0.144.4で生成したstable/v2 schemaおよび対象subtreeのSHA-256を [schema検証記録](codex-app-server-rate-limits-schema-2026-07-26.md) に記録
- 自動検証: Release solution build（警告0・エラー0）/ 356 tests / formatter / build contract 合格
- coverage: 91.41% (1979/2165 lines、package分離計測)
- 実アカウント受入: 未実施（usage値・金額・残高は記録しない）
