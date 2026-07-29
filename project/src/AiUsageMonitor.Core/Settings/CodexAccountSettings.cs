namespace AiUsageMonitor.Core.Settings;

public sealed record CodexAccountSettings
{
    public const string DefaultAccountId = "default";

    public string Id { get; init; } = DefaultAccountId;
    public string DisplayName { get; init; } = "CODEX";
    public string? CodexHomePath { get; init; }
    public bool Enabled { get; init; } = true;
    public bool ShowInWidget { get; init; } = true;

    public static CodexAccountSettings Default { get; } = new();
}
