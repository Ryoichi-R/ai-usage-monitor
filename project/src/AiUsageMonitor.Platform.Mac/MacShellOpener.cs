using System.Diagnostics;

namespace AiUsageMonitor.Platform.Mac;

/// <summary>Finderでフォルダーを開く（/usr/bin/open）。</summary>
public sealed class MacShellOpener : IShellOpener
{
    public void OpenFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var startInfo = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        startInfo.ArgumentList.Add(path);
        using Process? _ = Process.Start(startInfo);
    }
}
