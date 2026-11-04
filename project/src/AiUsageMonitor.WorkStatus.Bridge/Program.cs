using AiUsageMonitor.Platform;
using AiUsageMonitor.WorkStatus.Bridge;

IDesktopOrigin? origin = null;
#if WORK_STATUS_WINDOWS
if (OperatingSystem.IsWindows()) origin = new AiUsageMonitor.Platform.Windows.WorkStatus.WindowsDesktopOrigin();
#elif WORK_STATUS_MAC
if (OperatingSystem.IsMacOS()) origin = new AiUsageMonitor.Platform.Mac.WorkStatus.MacDesktopOrigin();
#endif
using var input = Console.OpenStandardInput();
return BridgeCommand.Execute(args, input, Console.Out, origin, DateTimeOffset.UtcNow);
