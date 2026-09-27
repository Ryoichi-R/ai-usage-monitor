using System.Runtime.Versioning;
using AiUsageMonitor.Platform.Mac;
using Avalonia;
using Avalonia.Controls;

namespace AiUsageMonitor.App.Mac;

[SupportedOSPlatform("macos")]
internal static class Program
{
    internal static bool DemoMode { get; private set; }

    internal static MacAppPathProvider Paths { get; } = new();

    [STAThread]
    public static int Main(string[] args)
    {
        // --demo: 固定データで層・透過の手動確認（P0-2）を行う。設定を読み書きせず、多重起動防止も使わない。
        DemoMode = args.Contains("--demo", StringComparer.Ordinal);
        if (DemoMode)
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

        using var instance = new MacSingleInstanceGuard(Paths.InstanceLockPath);
        if (!instance.TryAcquire())
        {
            // 常駐中のインスタンスがある。.app経由ならLaunchServicesが既存側を前面に出す。
            Console.Error.WriteLine("AI Usage Monitor is already running.");
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<MacApp>()
            .UsePlatformDetect()
            // Dockに出さない常駐アプリ（.appのLSUIElement=trueに相当）。
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .LogToTrace();
}
