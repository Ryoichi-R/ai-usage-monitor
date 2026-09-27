# Avalonia.Headless xUnit probe

This disposable probe confirms that the .NET 10 test runner can start Avalonia.Headless on macOS and exercise an Avalonia `Window` and arranged control without a native window server.

Run it with the cached NuGet packages and no package source access:

```sh
dotnet restore project/probes/avalonia-headless/Avalonia.Headless.Probe.csproj \
  --configfile project/probes/avalonia-headless/NuGet.Config
dotnet test project/probes/avalonia-headless/Avalonia.Headless.Probe.csproj \
  -c Release --no-restore -p:UsedAvaloniaProducts= --verbosity minimal
```

`UsedAvaloniaProducts` is empty in this probe command to skip Avalonia's build telemetry task, which otherwise writes under the user's Application Support directory. This does not change Avalonia runtime or test behavior.

## D4 test split

Use Avalonia.Headless xUnit tests for view models, bindings, control behavior, layout, and deterministic interaction logic. Keep Windows HWND behavior in the `App.Windows.Tests` real-window host. Validate Mac NSWindow, menu-bar, display-layout, and compositor behavior with an explicit Mac acceptance matrix because this plan does not yet define an `App.Mac.Tests` host. If a behavior such as full-screen topmost ordering cannot be observed reliably, retain it as a per-OS manual acceptance case.

## Mac Studio result (2026-09-27)

- macOS 27.0, Apple Silicon; .NET SDK 10.0.204.
- Avalonia.Headless.XUnit 11.3.20: 1 test passed, 0 failed.
- The test created a headless window, arranged a button, and verified dimensions and content.
- This confirms the test harness only; it does not validate native window levels, click-through, menu bar behavior, physical displays, or full-screen ordering.
