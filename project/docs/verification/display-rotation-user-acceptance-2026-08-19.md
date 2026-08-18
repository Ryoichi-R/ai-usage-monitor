# 画面回転のユーザー受入申告（2026-08-19）

## 結果

- 2026-08-19（Asia/Tokyo）、ユーザーから画面回転の実機受入に関する未解消事項は解消済みとの申告を受けた。
- 申告時点のrepository HEADは `6991a2206559e808881a5891ee7cf63a3d587794` だった。

## 未記録情報

次の情報は取得できておらず、申告対象candidateとの厳密なbindingは行わない。

- 実行したcandidate EXEのpath、ProductVersion、SHA-256
- Surface機種、Windows build、物理解像度、display orientation
- 横→縦→横、縦向き再起動、175% DPI、Standard／Compact、Custom／Preset、background各modeの個別結果
- EventSource trace、スクリーンショット、working area／information boundsの測定値

## 適用境界

この記録は、詳細証跡がないユーザー申告を履歴として残すものであり、未記録項目を実施済みとは推定しない。申告後にDPI取得と物理pixel配置の実装を変更したため、amend後candidateについては同じ実機受入を再実施し、candidate EXEのpath、ProductVersion、SHA-256と個別結果を別記録へ固定する必要がある。

## amend後candidateの実機再確認

### Candidate binding

- Repository commit: `cf9bf5ced0d96714ef60efe3d82e2365b9fa7522`
- EXE file name: `AiUsageMonitor.App.exe`（ローカルの一時出力先は公開記録へ含めない）
- ProductVersion: `0.1.0+cf9bf5ced0d96714ef60efe3d82e2365b9fa7522`
- FileVersion: `0.1.0.0`
- SHA-256: `1BF434E7C61A491ABF790E3E8BA62AD0BFEB796A39091D97E8686D001E2F27C3`

### 実機条件と結果

- 実施日: 2026-08-19（Asia/Tokyo）
- 端末表示: Surface Pro 12-inch 1st Edition with Snapdragon
- 表示倍率: 175%
- 横向き: 2196 x 1464（推奨）
- 縦向き: 1464 x 2196（推奨）
- 操作: 回転ロックを一時的にオンにし、横→縦→横を実施。各変更をWindows設定で確定した。
- 縦向き結果: ウィジェットが右下へ再配置され、右端・下端からはみ出さず、4区画、数値、リセット時刻が表示された。
- 横向き復帰結果: ウィジェットが右下へ再配置され、右端・下端からはみ出さず、4区画、数値、リセット時刻が表示された。
- 復元確認: 2196 x 1464、横向き、回転ロックオフへ復元した。

### 未実施・適用境界

- この再確認では、横→縦→横の表示再配置だけを対象とした。
- 縦向き再起動、Standard／Compact、Custom／Preset、background各mode、EventSource trace、working area／information boundsの数値採取は実施していない。
- 全画面キャプチャは画面上で確認したが、公開候補へ画像ファイルは追加していない。
