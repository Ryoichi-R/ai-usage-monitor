# Codex App Server rate limits schema 受領記録

- 実施日時: 2026-07-26T14:09:04+09:00 (Asia/Tokyo)
- Codex CLI: `codex-cli 0.144.4`
- 生成コマンド: `codex app-server generate-json-schema --out <temporary-directory>`
- 対象: stable `account/read`、`account/rateLimits/read`、`account/rateLimits/updated`

## bundle hash

- `codex_app_server_protocol.schemas.json`: `CE60B1CE3632C026F26C6EEDFD516AB2836384B6DCC20BDE540F5CFFBAA2CE21`
- `codex_app_server_protocol.v2.schemas.json`: `7F5BED1D9AF039C9AD8E96875153DD02567EFE4340B4AD6F3C23335CE78E150C`
- `v2/GetAccountRateLimitsResponse.json`: `26BED5356B0B7ABD8F3F5BDD2F55D16123E340B038AFEE26A35D5470F4C17F5B`

bundle全体のhashは無関係なRPC変更でも変わるため、互換性判定には次の正規化subtreeを使用する。

## 正規化subtree

```json
{"CreditsSnapshot":{"required":["hasCredits","unlimited"],"balance":["string","null"],"hasCredits":"boolean","unlimited":"boolean"},"GetAccountRateLimitsResponse":{"required":["rateLimits"],"rateLimits":"RateLimitSnapshot","rateLimitsByLimitId":["object<string,RateLimitSnapshot>","null"]},"RateLimitSnapshot":{"required":[],"credits":["CreditsSnapshot","null"],"individualLimit":["SpendControlLimitSnapshot","null"],"primary":["RateLimitWindow","null"],"secondary":["RateLimitWindow","null"]},"SpendControlLimitSnapshot":{"required":["limit","remainingPercent","resetsAt","used"],"limit":"string","remainingPercent":"integer:int32","resetsAt":"integer:int64","used":"string"}}
```

- UTF-8（BOMなし）SHA-256: `8CA71F99D1341B19770A57D704044C51201C876D90B8B5E8950DC58C846CD046`

## 照合結果

- `rateLimitsByLimitId` はnullable objectで、値は `RateLimitSnapshot`。実装は `codex` bucketを優先し、存在しない場合だけ後方互換の `rateLimits` を使用する。
- `credits` と `individualLimit` はそれぞれnullableかつ `RateLimitSnapshot` のrequiredではない。欠落時は他componentを維持し、当該カードだけを表示しない。
- `CreditsSnapshot` は `hasCredits` と `unlimited` がrequired。`balance` はstringまたはnullで、通貨codeは含まれない。
- `SpendControlLimitSnapshot` は `used`、`limit`、`remainingPercent`、`resetsAt` がrequired。金額文字列はInvariantCultureの非負decimalとして解釈する。
- stable method名 `account/read`、`account/rateLimits/read` と通知名 `account/rateLimits/updated` をbundle内で確認した。
- `rateLimitResetCredits` は使用上限リセット用の別概念であり、金銭的な残高・追加利用額として表示しない。

この記録にはaccount email、usage実値、金額、残高、token、認証ファイルまたは環境変数の内容を含めない。
