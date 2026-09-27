using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(AiUsageMonitor.App.UI.Tests.TestAppBuilder))]

namespace AiUsageMonitor.App.UI.Tests;

public sealed class TestApp : Application;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
