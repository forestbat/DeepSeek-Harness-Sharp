using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Gui.Views;

namespace Dsh.Tests;

/** 启动失败窗口: 连不上本机 Host 时 GUI 必须给出可见出口, 而不是留在"没有窗口的进程"里。 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class GuiStartupFailureTests
{
    [Fact]
    public Task StartupFailureWindow_ShowsTheReason_AndRetries() => HeadlessGui.RunAsync(async () =>
    {
        var window = new StartupFailureWindow("本机 Host 在 90 秒内没有完成握手：connection lost");
        var decision = window.ShowAndWaitAsync();
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains(texts, text => text?.Contains("connection lost", StringComparison.Ordinal) == true);

        window.GetVisualDescendants().OfType<Button>().Single(button => (string?)button.Content == "重试连接")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(await decision);
    });

    [Fact]
    public Task StartupFailureWindow_ClosingMeansQuit() => HeadlessGui.RunAsync(async () =>
    {
        var window = new StartupFailureWindow("boom");
        var decision = window.ShowAndWaitAsync();
        Dispatcher.UIThread.RunJobs();

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(await decision);
    });
}