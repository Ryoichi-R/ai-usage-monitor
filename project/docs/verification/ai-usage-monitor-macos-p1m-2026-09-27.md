# macOS Phase 1 P1-M acceptance — 2026-09-27

## Environment

- Mac Studio, Apple Silicon; macOS 27.0 (build 26A428)
- .NET SDK 10.0.204; PowerShell 7.6.6

## Results

| Check | Result |
|---|---|
| Restore and Release common tests | PASS — 371 tests across `AiUsageMonitor.Core.Tests` (154), `AiUsageMonitor.Claude.Tests` (62), `AiUsageMonitor.Codex.Tests` (62), `AiUsageMonitor.Claude.Cli.Tests` (89), and `AiUsageMonitor.Platform.Tests` (4) |
| Portable `FakeClaudeCli` | PASS — built as a test dependency and started by `ClaudeCliCapabilityProbe` using only its fake `--help` and `--version` responses |
| Common production line coverage | PASS — 2,508 / 2,742 lines (91.47%), excluding `bin/obj` and counting each production assembly once |

Coverage by production assembly:

| Assembly | Covered / valid | Coverage |
|---|---:|---:|
| `AiUsageMonitor.Core` | 489 / 521 | 93.86% |
| `AiUsageMonitor.Claude` | 449 / 494 | 90.89% |
| `AiUsageMonitor.Codex` | 907 / 984 | 92.17% |
| `AiUsageMonitor.Claude.Cli` | 648 / 728 | 89.01% |
| `AiUsageMonitor.Platform` | 15 / 15 | 100.00% |

`AiUsageMonitor.App.UI` has no executable implementation at this phase and contributes zero production lines. Test projects and support executables are excluded from the coverage population.

## Reproduction

```sh
dotnet test AiUsageMonitor.Mac.slnx -c Release

dotnet test AiUsageMonitor.Mac.slnx -c Release --no-build --no-restore -m:1 \
  --collect:'XPlat Code Coverage' --results-directory <temporary-directory-outside-project>
```

## Test-harness adjustments

- Shortened generated Claude pipe names so their Unix-domain socket paths stay below macOS's 104-character limit.
- Added a test-only `.exe` symlink for the Mach-O fake app-server. It lets current Codex integration tests pass an explicit executable path through the Windows-shaped locator until Phase 4 adds darwin-native production lookup.
- Added contract coverage for the shared Claude failure-code mapping, screen-session result values, executable metadata, and platform DTO fields.

## Gate status

P1-M is complete. This does not complete Phase 1: P1-W still requires Windows 11 execution, and Phase 0 remains before GO. Phase 2 must wait for both Phase 0 GO and P1-W.
