# Avalonia 12 Headless xUnit probe

This probe re-runs P0-8 on Avalonia 12.1.2, the version chosen for the product (plan decision D14). It confirms that Avalonia.Headless starts under the .NET 10 test runner on macOS. It also checks that a window arranges a control and that a binding follows view-model changes under the Fluent theme.

Avalonia 12's `Avalonia.Headless.XUnit` depends on xUnit v3 (`xunit.v3.extensibility.core` >= 3.2.2). The existing product tests use xUnit v2, so the Avalonia UI tests must live in a separate test project. The project is an executable (`OutputType=Exe`), as xUnit v3 requires.

```sh
dotnet restore project/probes/avalonia-headless-12/Avalonia.Headless.Probe12.csproj \
  --configfile project/probes/avalonia-headless-12/NuGet.Config
TESTINGPLATFORM_TELEMETRY_OPTOUT=1 dotnet test \
  project/probes/avalonia-headless-12/Avalonia.Headless.Probe12.csproj \
  -c Release --no-restore -p:UsedAvaloniaProducts=
```

`UsedAvaloniaProducts` is left empty to skip Avalonia's build telemetry task (`Avalonia.BuildServices`). `TESTINGPLATFORM_TELEMETRY_OPTOUT=1` disables the Microsoft.Testing.Platform telemetry that the test runner pulls in through `Microsoft.ApplicationInsights`.

## Pinned versions (2026-09-27)

| Package | Version | Published |
|---|---|---|
| Avalonia.Headless.XUnit, Avalonia.Themes.Fluent | 12.1.2 | 2026-09-02 |
| xunit.v3 | 3.2.2 | 2026-01-14 |
| xunit.runner.visualstudio | 3.1.4 | existing product version |
| Microsoft.NET.Test.Sdk | 17.14.1 | existing product version |

We skipped 12.1.3 (published 2026-09-22) and xunit.v3 4.0.1 (published 2026-09-12). Both were less than two weeks old at selection time.

The restore resolved 36 packages. The main groups are the Avalonia 12.1.2 packages, HarfBuzzSharp 8.3.1.3 native assets, MicroCom.Runtime, Avalonia.BuildServices 11.3.2 (build telemetry), the xUnit v3 3.2.2 packages, and the Microsoft test platform with its telemetry dependency.

## Mac Studio result (2026-09-27)

- Machine: macOS 27.0, Apple Silicon, .NET SDK 10.0.204.
- Result: 2 tests passed, 0 failed.
  - A headless window arranged a button with the expected dimensions.
  - A `TextBlock` binding followed a view-model change under `FluentTheme`.
- This confirms only the test harness and basic layout and binding. It does not validate native window levels, click-through, the menu bar or tray, physical displays, or full-screen ordering. P0-2 covers those behaviors and must be repeated on Avalonia 12 with manual checks.
