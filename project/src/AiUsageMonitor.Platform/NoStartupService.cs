namespace AiUsageMonitor.Platform;

// Explicit isolated acceptance mode must not change the user's startup registration.
public sealed class NoStartupService : IStartupService
{
    public void Apply(bool startWithSystem) { }
}
