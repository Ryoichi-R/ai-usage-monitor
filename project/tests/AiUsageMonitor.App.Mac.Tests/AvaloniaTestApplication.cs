using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(AiUsageMonitor.App.Mac.Tests.TestAppBuilder))]

namespace AiUsageMonitor.App.Mac.Tests;

public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public static class TestAppBuilder
{
    // Skia描画と同梱Interフォントで、文字幅を含むレイアウトを実測する。
    // AvaloniaObject（Control、Brush、Transformなど）を生成するテストは必ず[AvaloniaFact] / [AvaloniaTheory]
    // にする。通常の[Fact]で生成すると別スレッドがUIスレッドとして確保され、Headlessセッションの
    // 後片付けがスレッド不一致で失敗する。
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
