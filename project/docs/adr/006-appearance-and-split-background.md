# ADR-006: 外観設定と最背面背景の分離

## Status

Accepted — 2026-08-01

## Context

利用率ウィジェットの色・フォント・背景を設定画面から変更できるようにし、背景を情報表示の背面へ拡張する。常に手前に表示する設定と組み合わせた場合でも、背景が他ウィンドウを覆わないことが必要である。

## Decision

- 外観値は `AppSettings` に保存し、保存前に `AppearanceSettingsValidator` で正規化・検証する。
- 背景は `BackgroundPresentationPolicy` で `None` / `Inline` / `Split` を決定する。Split は背景有効、透明度が正、常に手前、かつ「他ウィンドウの背面に隠す」が有効な場合だけ選択する。
- Inline はメインウィンドウ内で描画し、EdgeFade は上下左右の四辺をマスク付きブラシで緩やかにぼかす。情報領域は背景外形の中央に置き、Split は別の非アクティブ・クリック透過ウィンドウで描画する。
- Split 背景は `BottomMostStrategy` と `WindowLayerRepairEngine` により `HWND_BOTTOM` を再適用する。復旧が連続して失敗した場合は背景を一時的に透明化し、情報表示を残す。
- 保存は候補設定を先に永続化し、成功後にだけメインウィンドウ、通知領域、取得サービス、Split 背景へ適用する。保存失敗時は実行中設定を変更しない。

## Consequences

外観設定は既存のスキーマと拡張データを維持したまま追加される。Split の厳密なZ-orderはWindowsの別TopMostウィンドウやセキュアデスクトップの影響を受けるため、修復失敗時の透明化と再試行を安全側の既定とする。Split 背景の最終的な視認性はWindows実機で確認する。
