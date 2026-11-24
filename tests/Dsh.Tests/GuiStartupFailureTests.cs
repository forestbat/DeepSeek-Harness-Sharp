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
        var window = new StartupFailureWindow(new StartupFailure(
            "连不上本机 Host，主窗口没有打开。", "本机 Host 在 90 秒内没有完成握手：connection lost", Connection: true));
        var decision = window.ShowAndWaitAsync();
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains(texts, text => text?.Contains("connection lost", StringComparison.Ordinal) == true);
        Assert.Contains(texts, text => text?.Contains("host.port", StringComparison.Ordinal) == true);

        window.GetVisualDescendants().OfType<Button>().Single(button => (string?)button.Content == "重试连接")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(await decision);
    });

    /** 连接没问题、只是会话打不开时不许把用户引去杀宿主进程: 那条建议只对"连不上"成立。 */
    [Fact]
    public Task StartupFailureWindow_SessionFailure_DoesNotBlameTheHostProcess() => HeadlessGui.RunAsync(() =>
    {
        var window = new StartupFailureWindow(new StartupFailure(
            "主窗口没有打开。", "session event 0 is not resident and page 0 could not be read back", Connection: false));
        _ = window.ShowAndWaitAsync();
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToList();
        Assert.Contains(texts, text => text?.Contains("page 0 could not be read back", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(texts, text => text?.Contains("host.port", StringComparison.Ordinal) == true);
        window.Close();
        Dispatcher.UIThread.RunJobs();
        return Task.CompletedTask;
    });

    [Fact]
    public Task StartupFailureWindow_ClosingMeansQuit() => HeadlessGui.RunAsync(async () =>
    {
        var window = new StartupFailureWindow(new StartupFailure("主窗口没有打开。", "boom", Connection: true));
        var decision = window.ShowAndWaitAsync();
        Dispatcher.UIThread.RunJobs();

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(await decision);
    });
}