using AiUsageMonitor.Claude.Usage;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.App.UI.Runtime;

// Phase 2 port of the WPF App's ClaudeRuntimeSnapshot. The runtime that produces it moves to
// App.UI in a later Phase 2 step; until then the WPF App keeps its own copy.
internal readonly record struct ClaudeRuntimeSnapshot(
    long Generation,
    UsageSnapshot Snapshot,
    ClaudeUsageSelection Selection)
{
    public ClaudeRuntimeSnapshot(long generation, UsageSnapshot snapshot)
        : this(generation, snapshot, new(
            snapshot,
            null,
            false,
            "LEGACY",
            ClaudeUsageFreshnessKind.None,
            null,
            null,
            null))
    {
    }
}
