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

### 実機条件と結果（訂正: 不合格）

- 実施日: 2026-08-19（Asia/Tokyo）
- 端末表示: Surface Pro 12-inch 1st Edition with Snapdragon
- 表示倍率: 175%
- 横向き: 2196 x 1464（推奨）
- 縦向き: 1464 x 2196（推奨）
- 初回確認の訂正: 単一起動制御により、このcandidateではなく既存の `ecd758571db483341b4c9a55388a940aa903c849` 版が表示されていたため、初回の合格判断を取り消す。
- 再実施: 既存版を停止し、上記SHA-256のcandidateだけが実行中であることをプロセスの絶対パスで確認してから、横→縦を実施した。
- 縦向き結果: **不合格**。ウィジェットの右列が画面右端の外へ残り、内容が切れた。高解像度の全画面キャプチャで確認した。
- 数値確認: candidateの実HWNDは物理pixelで `1293,1879,1490,2177`、working areaは `0,0,1464,2196`、DPIは168で、右端が26px超過していた。
- 追加切り分け: 成功後settle retryと実HWND最終clampを追加した候補でもHWND矩形が上記値から変化しなかったため、回転時の再配置callback自体が起動していないと判断した。
- 原因: この端末の回転経路ではMainWindowのHWND hookへ表示変更通知が届かず、`WM_DISPLAYCHANGE`／`SPI_SETWORKAREA` だけではwaveを開始できなかった。`SystemEvents.DisplaySettingsChanged` を独立した通知経路として追加し、同じschedulerへ集約する必要がある。
- 復元確認: 2196 x 1464、横向き、回転ロックオフへ復元した。

### 未実施・適用境界

- この再確認では、横→縦の表示再配置で不合格を確認した時点で中止し、横向きへ復元した。
- 縦向き再起動、Standard／Compact、Custom／Preset、background各mode、EventSource trace、working area／information boundsの数値採取は実施していない。
- 全画面キャプチャは画面上で確認したが、公開候補へ画像ファイルは追加していない。

## 最終candidateの実機再確認（合格）

### Candidate binding

- Repository commit: `7972748a462bc291f320fc65f7c5a6095c60c017`
- EXE file name: `AiUsageMonitor.App.exe`（ローカルの一時出力先は公開記録へ含めない）
- ProductVersion: `0.1.0+7972748a462bc291f320fc65f7c5a6095c60c017`
- SHA-256: `7A6D088B834B4514080AE522A149295E868CCE0FFEA4DC9194AC94E5787FEF6D`
- 上記candidateだけが実行中であることを、実行プロセスの絶対パスで確認した。

### 実機条件と結果

- 実施日: 2026-08-19（Asia/Tokyo）
- 端末表示: Surface Pro 12-inch 1st Edition with Snapdragon
- 表示倍率: 175%
- 横向き: 2196 x 1464（推奨）
- 縦向き: 1464 x 2196（推奨）
- 横向きから縦向きへ回転し、5秒待機後に確認した。
- 縦向き結果: **合格**。ウィジェットの右端を含むCPU、MEM、NET、BAT、CODEX、CLAUDEのラベル、値、reset表示が画面内に収まることを高解像度の全画面キャプチャで確認した。
- 数値確認: candidateの実HWNDは物理pixelで `1267,1879,1464,2177`、working areaは `0,0,1464,2196`、DPIは168で、全辺がworking area内に収まった（`inside=True`）。
- 復元確認: candidateを終了し、2196 x 1464、横向き、回転ロックオフへ復元した。既存インストール版が元のパスから単独で起動していることも確認した。

### 適用境界

- 合格判定は上記 `7972748a462bc291f320fc65f7c5a6095c60c017` candidateにだけ適用する。先行する不合格candidateは受入済みとして扱わない。
- 全画面キャプチャはローカル検証証跡として保持し、公開候補へ画像ファイルは追加していない。
- 今回の再確認範囲は横向きから縦向きへの再配置である。縦向き再起動、全表示modeの組合せ、EventSource traceは追加実施していない。
