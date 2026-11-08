using System.Text.Json;
using AiUsageMonitor.Core.WorkStatus;
using AiUsageMonitor.Platform;
using AiUsageMonitor.WorkStatus;

namespace AiUsageMonitor.WorkStatus.Bridge;

public static class BridgeCommand
{
    public static int Execute(string[] args, Stream input, TextWriter output, IDesktopOrigin? origin, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        try
        {
            if (args.Length is not (2 or 3) || !Enum.TryParse(args[0], out WorkProvider provider) || !Enum.IsDefined(provider) ||
                !Path.IsPathFullyQualified(args[1])) return 0;
            // The optional flag is written only by HookExamples after StopHookInspector approved the Claude project.
            bool stopMayComplete = args.Length == 3;
            if (stopMayComplete && (args[2] != HookExamples.StopMayCompleteFlag || provider != WorkProvider.ClaudeCode)) return 0;
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int count;
            while ((count = input.Read(chunk)) > 0)
            {
                if (buffer.Length + count > 4 * 1024 * 1024) return 0;
                buffer.Write(chunk, 0, count);
            }
            DesktopApplication application = provider == WorkProvider.CodexDesktop ? DesktopApplication.Codex : DesktopApplication.Claude;
            if (origin is null || !origin.IsDesktopAncestor(application)) return 0;
            using var document = JsonDocument.Parse(buffer.ToArray());
            WorkStatusEvent? value = HookNormalizer.Normalize(document.RootElement, provider, true, now, stopMayComplete);
            if (value is not null) WorkStatusCollector.WriteEvent(args[1], value);
        }
        catch (Exception) { /* No host decisions or private exception output. */ }
        finally { output.WriteLine("{}"); }
        return 0;
    }
}
