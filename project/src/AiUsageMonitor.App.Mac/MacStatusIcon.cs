using Avalonia.Controls;

namespace AiUsageMonitor.App.Mac;

/// <summary>アプリと同じ画像を使うメニューバー用アイコン。</summary>
internal static class MacStatusIcon
{
    public static WindowIcon Create()
    {
        using var stream = typeof(MacStatusIcon).Assembly.GetManifestResourceStream(
            "AiUsageMonitor.App.Mac.AppIcon.ico")
            ?? throw new InvalidOperationException("Application icon resource is missing.");
        return new WindowIcon(stream);
    }
}
