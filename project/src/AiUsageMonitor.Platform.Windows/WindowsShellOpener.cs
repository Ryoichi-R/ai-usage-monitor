using System.Diagnostics.CodeAnalysis;
using AiUsageMonitor.Platform;
using DiagnosticsProcess = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace AiUsageMonitor.Platform.Windows;

/// <summary>explorer.exeでフォルダーを開くIShellOpenerのWindows実装。</summary>
[ExcludeFromCodeCoverage(Justification = "Launches the real explorer.exe process; verified by interactive UI testing.")]
public sealed class WindowsShellOpener : IShellOpener
{
    public void OpenFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var process = DiagnosticsProcess.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
        {
            UseShellExecute = true,
        });
    }
}
