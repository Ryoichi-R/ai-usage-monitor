using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(AiUsageMonitor.AvaloniaHeadlessProbe.TestAppBuilder))]

namespace AiUsageMonitor.AvaloniaHeadlessProbe;

public sealed class TestApp : Application;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class HeadlessTests
{
    [AvaloniaFact]
    public async Task HeadlessWindowOpensAndArrangesAControl()
    {
        var button = new Button { Content = "refresh", Width = 120, Height = 40 };
        var window = new Window { Content = button, Width = 240, Height = 100 };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.True(window.IsVisible);
        Assert.Equal(240, window.Bounds.Width);
        Assert.Equal(120, button.Bounds.Width);
        Assert.Equal("refresh", button.Content);
        window.Close();
    }
}
