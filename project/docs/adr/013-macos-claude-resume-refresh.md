# ADR 013 macOSのスリープ復帰後にClaudeの利用枠を再取得する

## 状態

採用（2026-11-12）

## 背景

Claude CLIの取得がスリープの前後で失敗すると、通常のバックオフが最大60分まで延び、操作再開後も利用枠の表示が古いままになる。

## 決定

- `Platform.Mac` は3秒周期で `mach_continuous_time` と `mach_absolute_time` の経過差を計測する。10秒を超える差で本体スリープと判定する。差の計算にはUTCを使用しない。`mach_continuous_time` はmacOS 10.12から利用でき、配布対象のApple Silicon Macで利用可能。AppleのRequired Reason APIの起動時刻カテゴリについて、アプリ内での経過時間の計測に対応する理由 `35F9.1` をバンドルの `Contents/Resources/PrivacyInfo.xcprivacy` に記載する。AppleはRequired Reason APIの申告が必要なOSとしてiOSなどを挙げている。macOS版にも用途を明示するため同梱する。[Appleのプライバシーマニフェスト案内](https://developer.apple.com/documentation/BundleResources/privacy-manifest-files?changes=__6&language=objc) · [AppleのAPI定義と申告理由](https://developer.apple.com/documentation/bundleresources/app-privacy-configuration/nsprivacyaccessedapitypes/nsprivacyaccessedapitype)
- メイン画面がオンラインで消灯していれば、点灯まで復帰要求を1件保留する。オンラインの画面がない場合や状態が不明な場合は取得を許可する。
- [ADR 011](011-platform-abstraction-boundary.md)に従い、`MacComposition` が `WidgetHostServices` の任意の監視開始関数を設定する。共有ホストは復帰要求でClaudeの定期取得待機を解除し、先行する取得が完了した後で既存の手動取得と同じ再試行リセットを使う。復帰要求はClaudeだけを対象とする。互換性停止と安全停止、未完了の取得記録、公式CLI実体の検証は通常取得と同じ契約に従う。
- .NETのmacOSのタイマーは `CLOCK_UPTIME_RAW` を基準とするため、本体スリープ中は定期取得のタイムアウトが進まない。復帰シグナルはタイムアウトの経過に関係なく待機を解除する。[.NETのタイマーキュー](https://github.com/dotnet/runtime/blob/v10.0.8/src/libraries/System.Private.CoreLib/src/System/Threading/TimerQueue.Unix.cs) · [.NETの単調時計](https://github.com/dotnet/runtime/blob/v10.0.8/src/native/minipal/time.c#L85)
- 診断には検出、消灯による保留、取得開始の理由コードを記録する。監視を終了してから取得サービスを破棄する。

## 影響

操作再開後、旧バックオフと定期取得タイマーの満了を待たずに再取得できる。検出は最大で数秒遅れ、10秒以下の本体スリープや画面だけの消灯では定期取得が続く。復帰時の通信失敗には通常のバックオフを適用する。
