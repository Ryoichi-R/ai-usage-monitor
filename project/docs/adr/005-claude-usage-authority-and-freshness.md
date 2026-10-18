# ADR 005: Claude usageの優先観測とfreshnessを分離する

## Status

Accepted (2026-07-27)

## Context

statusLineの`refreshInterval`はローカルcommandの再実行時刻であり、payloadにserver measurement timestampはない。従来のAutomaticはfresh判定により周期的な`/usage`取得を省略し、設定300秒に対して実効最大約10分の遅延を生じさせた。

## Decision

ADR 003の次の3決定を部分改訂する。

1. 「Automaticはfreshなpassive観測を優先する」を、毎周期およびmanualで公式CLI `/usage`を試行し、正常な`CliScreen`をアプリ内の優先観測とする決定へ置換する。
2. 「usage観測状態は単一mergerが所有する」を、runtime所有のstate store内にactive/passiveの独立mergerを置く決定へ置換する。
3. 「旧active完了は共有mergerの時刻規則で決める」を、runtimeがgenerationとcoordinator identityを確認した後だけactive channelへcommitする決定へ置換する。

Automaticはfresh activeを通常表示し、それが表示不能でfresh passiveがある場合だけ「statusLine参考値 — 最新性未保証」と明示してfallbackする。OfficialCliOnlyはpassiveを受信しても選択しない。StatusLineOnlyはactive CLIを起動しない。

active TTLは`max(10分, refresh interval × 2.2)`、passive TTLはpipe受信から10分とし、reset通過をTTLより優先する。HUDの`CLI`は`/usage`画面の読取り完了時刻、`SL受信`はpipe delivery時刻であり、いずれもserver measurement timestampではない。

## Unchanged decisions

ADR 003のactive起動引数、署名・trust境界、Job Object、unknown画面のfail-closed、認証情報を読まないprivacy境界、reset validation、使用率実値をdiskへ保存しない決定は置換しない。
