using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(AiUsageMonitor.AvaloniaHeadlessProbe12.TestAppBuilder))]

namespace AiUsageMonitor.AvaloniaHeadlessProbe12;

public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class UsageModel : INotifyPropertyChanged
{
    private string _label = "Waiting";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label
    {
        get => _label;
        set
        {
            _label = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }
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

    [AvaloniaFact]
    public async Task BindingFollowsViewModelChangesUnderFluentTheme()
    {
        var model = new UsageModel();
        var text = new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(UsageModel.Label)) };
        var window = new Window { Content = text, DataContext = model, Width = 200, Height = 80 };
        window.Show();
        await Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.Equal("Waiting", text.Text);

        model.Label = "42% used";
        await Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.Equal("42% used", text.Text);
        Assert.True(text.Bounds.Width > 0);
        window.Close();
    }
}
