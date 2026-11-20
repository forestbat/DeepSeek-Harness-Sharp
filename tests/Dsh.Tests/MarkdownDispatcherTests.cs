using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Dsh.Gui.Views;

namespace Dsh.Tests;

[Collection(GuiSerialCollection.CollectionName)]
public sealed class MarkdownDispatcherTests
{
    [Fact]
    public async Task MarkdownFallback_RendersAfterItsOriginalDispatcherIsDisposed()
    {
        await HeadlessGui.RunAsync(() => Assert.IsType<Dsh.Gui.App>(Application.Current));
        var original = await RenderOnNewDispatcherAsync();
        var replacement = await RenderOnNewDispatcherAsync();
        Assert.NotSame(original, replacement);
        await HeadlessGui.RunAsync(() =>
        {
            Assert.IsType<Dsh.Gui.App>(Application.Current);
            var markdown = new MarkdownView { Text = "Shared GUI restored." };
            Assert.NotEmpty(Assert.IsType<StackPanel>(markdown.Content).Children);
        });
    }

    private static async Task<Dispatcher> RenderOnNewDispatcherAsync()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(MarkdownFallbackAppBuilder), AvaloniaTestIsolationLevel.PerTest);
        var rendered = session.Dispatch(() =>
        {
            var markdown = new MarkdownView { Text = "```text\nhello\n```" };
            var window = new Window { Content = markdown };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var root = Assert.IsType<StackPanel>(markdown.Content);
                var code = Assert.Single(root.Children.OfType<Border>());
                var background = Assert.IsAssignableFrom<ISolidColorBrush>(code.Background);
                Assert.Equal(Color.FromArgb(0x28, 0x80, 0x80, 0x80), background.Color);
                Assert.Equal(background.Color.ToString(), background.ToString());
                return Dispatcher.UIThread;
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }, TestContext.Current.CancellationToken);
        return await rendered.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
    }
}

public static class MarkdownFallbackAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}
