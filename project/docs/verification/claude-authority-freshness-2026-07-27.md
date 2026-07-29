# Claude authority / freshness implementation receipt

- Date: 2026-07-27 (Asia/Tokyo)
- Scope: Claude active/passive source arbitration, refresh semantics, freshness labels, remaining UI
- Privacy: usage percentages, raw CLI screens, account data, session IDs, cwd, and transcript paths are not recorded here.

## Implemented

- Runtime-owned independent active/passive channel state and pure mode selector.
- Generation/coordinator identity check before active commit.
- Automatic and OfficialCliOnly active polling; manual refresh bypasses freshness and periodic backoff.
- Shared active invocation with waiter-only cancellation.
- Active TTL `max(10 minutes, interval × 2.2)` and passive receipt TTL 10 minutes.
- Automatic passive fallback marked as a non-authoritative reference with active failure metadata.
- OfficialCliOnly passive ingestion without passive selection.
- StatusLineOnly two-minute no-receipt diagnostic.
- HUD `CLI` / `CLI最終` and `SL受信` / `SL最終` semantics.
- Coalesced manual refresh during the Codex phase and join during the Claude phase.
- Production coordinator API consolidated to a single side-effect-free active outcome path.
- Backoff skips no longer advance active attempt/failure metadata.
- Settings separates selected source, active last attempt/failure, and statusLine last receipt.
- HUD tooltips explain that CLI/SL timestamps are not server measurement timestamps.
- AC-26 measurement script tracks owned process trees by PID, creation time, and ancestry.
- Persistent `残り` label in the shared usage row template.
- README, troubleshooting, privacy, ADR 003 amendment, and ADR 005.

## Validation

- `dotnet build .\AiUsageMonitor.slnx -c Release --no-restore`: passed, 0 warnings / 0 errors.
- `dotnet test .\AiUsageMonitor.slnx -c Release`: passed before final focused additions.
- `dotnet test .\AiUsageMonitor.slnx -c Release --no-build --no-restore --disable-build-servers`: passed after review fixes.
  - Core 114
  - Claude 55
  - Codex 59
  - Windows 6
  - Claude.Windows 88
  - App 138
  - Total 460, failures 0
- `dotnet format .\AiUsageMonitor.slnx --verify-no-changes --no-restore`: passed.
- `pwsh -NoProfile -File .\scripts\publish-ai-usage-monitor.ps1 -Runtime win-x64`: passed.
- `pwsh -NoProfile -File .\scripts\publish-ai-usage-monitor.ps1 -Runtime win-arm64`: passed.
- `pwsh -NoProfile -File .\scripts\test-build-contract.ps1`: passed.
- `pwsh -NoProfile -File .\scripts\measure-claude-active-source.ps1 -Count 5 -StartupTimeoutSeconds 30`: passed.
  - signed publisher: Anthropic, PBC
  - final published-helper run wall time: 6.686–7.198 seconds; p50 6.810 seconds
  - timeout: 0
  - maximum concurrent active session roots: 1
  - owned processes remaining after 10 seconds: 0 for every iteration

## Remaining environment-dependent acceptance

- Manual comparison among Automatic, StatusLineOnly, OfficialCliOnly, and a live Claude Code `/usage` screen was not run.
- No claim about server measurement timestamp or backend aggregation latency is made.
