# ADR 009: macOSにおけるウィジェット層制御

## Status

Accepted (2026-09-27)。macOS実機での受入は未了。

## Context

Windows版はuser32の`SetWindowPos`と`WS_EX_TRANSPARENT`で常に手前・クリック透過を実装し、背景windowを最背面へ保つ。Avaloniaの標準APIはmacOSでの最背面・Spacesの挙動・クリック透過の読み戻しを提供しない。

## Decision

- `AiUsageMonitor.Platform.Mac`の`MacWidgetLayerController`が、Avaloniaから得た`NSWindow`（または`NSView`の所属window）へobjc runtime経由で`level`・`ignoresMouseEvents`・`collectionBehavior`を設定する。objc interopは`Platform.Mac`内に閉じ込める。
- levelは`CGWindowLevelForKey`で求める（常に手前: floating、最背面: desktop、通常: normal）。設定後にnative値を読み戻し、不一致なら層の状態を劣化（degraded）として診断ログへ`widget-layer-degraded`を記録する。
- Dockに出さない常駐アプリとし（`LSUIElement=true`）、操作はメニューバーのアイコンから行う。ログイン時の自動起動は`~/Library/LaunchAgents`のplistで行う。
- 多重起動は専用lock fileへの`flock`で防ぐ。statusLine受信用socketとは別のpathと責務にする。

## Consequences

- full-screenの他アプリとの前後関係など、自動観測が不安定な挙動は手動受入に残す。
- Avaloniaの内部実装（native handleの種類）が変わると動かなくなりうる。Avaloniaの版を変えたら手動受入表を再実施する。
