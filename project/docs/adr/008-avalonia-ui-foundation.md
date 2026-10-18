# ADR 008: UI基盤をAvaloniaへ移行する

## Status

Accepted (2026-09-27)。Windows実機での同等性受入とmacOS実機受入は未了で、受入後に確定とする。

## Context

WPF版はmacOSで動作しない。macOSへ移植する条件（計画D1）は、Windows／macOSで単一のUIコードベースにすることである。移植の前にWPF版の抽象境界を`AiUsageMonitor.Platform`と`AiUsageMonitor.Claude.Cli`へ分離済みである。

## Decision

- UIは`AiUsageMonitor.App.UI`（Avalonia 12.1.2、`net10.0`）へ一本化する。OS別の`App.Windows`／`App.Mac`はcomposition rootだけを持つthin hostとし、`App.UI`はOS別projectを参照しない（D9）。
- Avaloniaは12系を使い、導入時点で公開から2週間以上経った12.1系の最新patchとする（D14）。build時telemetry（Avalonia.BuildServices）は`Directory.Build.targets`で停止する。
- 層（常に手前・最背面・通常）とクリック透過は`IWidgetLayerController`だけが所有する。UI層はAvaloniaの`Topmost`を直接読み書きしない。macOSでは最背面をnative window levelで実装するため、`Topmost`とnative状態が一致しないことが分かっている。
- 座標はAvaloniaのscreen座標を使う。Windowsは物理pixel、macOSはpointであり、保存と再配置はそのOSの単位で行う。
- UI testはAvalonia.Headless（xUnit v3）で共通ロジックを検証する。Windowsのnative挙動は`App.Windows.Tests`の実window host、macOSのnative window・menu bar・compositorは手動受入表で検証する（D4）。
- 旧WPF版は`AiUsageMonitor.App.WpfLegacy`として並存させ、Windows受入の完了後に削除する。Windowsの配布経路（publish／rebuild／導入BAT）は既定でAvalonia版を使い、`-UseLegacyWpf`でWPF版へ戻せる。既定のままpushする前にWindows実機受入を行う（D15）。

## Consequences

- WPF依存のUI testは意図を移植し、単純削除しない。
- macOS 27はAvaloniaの公式対応表に未掲載である。Avaloniaの版を変えたときとmacOSを更新したときに、Macの手動受入表を再実施する（D10）。
- 同梱する第三者コンポーネントと原文は`THIRD-PARTY-NOTICES.md`と`licenses/`に記載する。
