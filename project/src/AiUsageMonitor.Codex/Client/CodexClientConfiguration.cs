using AiUsageMonitor.Platform;

namespace AiUsageMonitor.Codex.Client;

public sealed record CodexClientConfiguration(
    string? ExecutablePath,
    string? CodexHomePath,
    TimeSpan StartupTimeout,
    Func<System.Diagnostics.Process, IDisposable>? ProcessLifetimeGuardFactory = null,
    IManagedProcessLauncher? ProcessLauncher = null);
