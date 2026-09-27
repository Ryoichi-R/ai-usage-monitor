# Third-party notices

本実装は.NETおよびCodex CLIと相互運用します。`desktop-system-monitor` の設計上の知見を参照しましたが、実行時参照やソースファイルの直接コピーは行っていません。

## 同梱する第三者コンポーネント

Avalonia版（Windowsの`App.Windows`、macOSの`AI Usage Monitor.app`）は次のコンポーネントを同梱します。第一者コードのMIT Licenseとは別に、各コンポーネントのライセンスが適用されます。ライセンス原文は[licenses/](licenses/)にあり、macOSの`.app`では`Contents/Resources/licenses/`に同梱します。版と著作権表示は2026-09-27にNuGetパッケージのmetadataと上流repositoryの該当commitで確認しました。

| コンポーネント | 版 | ライセンス | 著作権表示 | 原文 |
| --- | --- | --- | --- | --- |
| Avalonia（Avalonia、Desktop、Native、Skia、HarfBuzz、Themes.Fluent、Controls.ColorPicker、Win32、X11、FreeDesktop、FreeDesktop.AtSpi、Remote.Protocol） | 12.1.2 | MIT | Copyright (c) AvaloniaUI OÜ | [Avalonia-LICENSE.md](licenses/Avalonia-LICENSE.md) |
| SkiaSharp（NativeAssetsを含む） | 3.119.4 | MIT | Copyright (c) 2015-2016 Xamarin, Inc. / Copyright (c) 2017-2018 Microsoft Corporation | [SkiaSharp-LICENSE.txt](licenses/SkiaSharp-LICENSE.txt) |
| HarfBuzzSharp（NativeAssetsを含む） | 8.3.1.3 | MIT | 同上 | [HarfBuzzSharp-LICENSE.txt](licenses/HarfBuzzSharp-LICENSE.txt) |
| SkiaSharp・HarfBuzzSharpのnative libraryに含まれる第三者成果物（Skia、HarfBuzz、libpng、FreeType、expat、ICU、libjpeg-turbo等） | 同上 | 各ライセンス | 各原文に記載 | [SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt](licenses/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt) |
| MicroCom.Runtime | 0.11.6 | MIT | Copyright (c) 2021 Nikita Tsukanov | [MicroCom.Runtime-LICENSE.txt](licenses/MicroCom.Runtime-LICENSE.txt) |
| Tmds.DBus.Protocol | 0.94.1 | MIT | Copyright 2006 Alp Toker、2010 Other Contributors、2016 Tom Deseyn | [Tmds.DBus-COPYING.txt](licenses/Tmds.DBus-COPYING.txt) |
| ANGLE（Avalonia.Angle.Windows.Natives、Windowsのみ） | 2.1.27548.20260419 | BSD-3-Clause | Copyright 2018 The ANGLE Project Authors | [ANGLE-LICENSE.txt](licenses/ANGLE-LICENSE.txt) |
| .NET runtime（自己完結型publishで同梱） | 10.0 | MIT | Copyright (c) .NET Foundation and Contributors | [dotnet-runtime-LICENSE.txt](licenses/dotnet-runtime-LICENSE.txt)、[dotnet-runtime-THIRD-PARTY-NOTICES.txt](licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt) |

build時だけ使う`Avalonia.BuildServices`（telemetryは`Directory.Build.targets`で停止）とテスト用packageは配布物に含めません。WPF版（`App.WpfLegacy`）は上表のAvaloniaを含みません。
