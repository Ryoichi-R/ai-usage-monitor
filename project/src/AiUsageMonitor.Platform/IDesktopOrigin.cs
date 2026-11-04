namespace AiUsageMonitor.Platform;

public enum DesktopApplication { Codex, Claude }

public interface IDesktopOrigin
{
    bool IsDesktopAncestor(DesktopApplication application);
}
