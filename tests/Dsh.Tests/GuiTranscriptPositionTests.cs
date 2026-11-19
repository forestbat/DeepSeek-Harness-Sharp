using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Dsh.Core;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.Llm;

namespace Dsh.Tests;

/**
 * 会话区的阅读位置: 切走再切回来要回到原来看到的地方, 不允许被布局或贴底顺手拉到最新;
 * 第一次看某条会话(没有记录)则相反, 长会话也要一次滑到最新。
 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class GuiTranscriptPositionTests
{
    private const int HistoryMessages = 14;
    private const double WindowWidth = 1280;
    private const double WindowHeight = 800;
    private const double StickyTolerance = 24;

    /** 回到会话不许贴底: 位置要还原到切走时的地方。 */
    [Fact]
    public Task ReturningToASession_RestoresTheReadingPosition() => HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        for (var index = 0; index < HistoryMessages; index++)
            session.Append(new UserMessagePayload(MessageFactory.CreateUserText($"历史 {index}\n第二行\n第三行")), new SurfaceOp.Append());
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
        await GuiTestEnvironment.WaitForAsync(viewModel, () => viewModel.Messages.Count == HistoryMessages);

        var chat = new ChatView { DataContext = viewModel };
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = chat };
        window.Show();
        try
        {
            Render(window);
            var list = chat.FindControl<ListBox>("MessageList")!;
            var scroll = Assert.IsType<ScrollViewer>(list.Scroll);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "precondition: the transcript must overflow the viewport");

            // 读者停在半途(不是最新): 这就是切换会话时要记住的"上次访问位置"。
            // 跟随意图只由真实手势改判(程序写 offset 不算), 所以这里像用户那样滚一格。
            var wheelPoint = scroll.TranslatePoint(new Point(scroll.Bounds.Width / 2, scroll.Viewport.Height / 2), window)!.Value;
            var tailOffset = scroll.Offset.Y;
            for (var attempt = 0; attempt < 8 && scroll.Offset.Y >= tailOffset; attempt++)
            {
                window.MouseWheel(wheelPoint, new Vector(0, 1));
                Render(window);
            }
            var readingOffset = scroll.Offset.Y;
            Assert.True(readingOffset > 0, "precondition: the reader must be away from the tail");
            Assert.True(chat.FindControl<Button>("JumpToLatest")!.IsVisible, "the tail button appears once the viewport left the tail");

            var first = viewModel.SelectedSession!;
            viewModel.NewSessionCommand.Execute(null);
            await GuiTestEnvironment.WaitForAsync(viewModel,
                () => viewModel.SelectedSession is { } selected && selected.SessionId != first.SessionId);
            Render(window);

            viewModel.SelectedSession = first;
            await GuiTestEnvironment.WaitForAsync(viewModel, () => viewModel.Messages.Count == HistoryMessages);
            for (var pass = 0; pass < 5; pass++)
                Render(window);

            Assert.True(scroll.Offset.Y + scroll.Viewport.Height < scroll.Extent.Height - StickyTolerance,
                $"returning to a session must not stick to the tail: offset {scroll.Offset.Y}, viewport {scroll.Viewport.Height}, extent {scroll.Extent.Height}");
            Assert.True(Math.Abs(scroll.Offset.Y - readingOffset) < scroll.Viewport.Height / 2,
                $"the reading position must be restored: saved {readingOffset}, restored {scroll.Offset.Y}");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /** 没有阅读记录的会话(第一次看): 长会话也要一次滑到最新, 不能停在会话头。 */
    [Fact]
    public Task OpeningALongSessionWithoutARecord_SlidesToTheNewestMessage() => HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        for (var index = 0; index < HistoryMessages; index++)
            session.Append(new UserMessagePayload(MessageFactory.CreateUserText($"历史 {index}\n第二行\n第三行")), new SurfaceOp.Append());
        await using var viewModel = new MainViewModel(environment.App, await environment.OpenChatAsync());
        await GuiTestEnvironment.WaitForAsync(viewModel, () => viewModel.Messages.Count == HistoryMessages);

        var chat = new ChatView { DataContext = viewModel };
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = chat };
        window.Show();
        try
        {
            for (var pass = 0; pass < 5; pass++)
                Render(window);
            var list = chat.FindControl<ListBox>("MessageList")!;
            var scroll = Assert.IsType<ScrollViewer>(list.Scroll);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "precondition: the transcript must overflow the viewport");
            Assert.True(scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - StickyTolerance,
                $"a session seen for the first time must open at the newest message: offset {scroll.Offset.Y}, viewport {scroll.Viewport.Height}, extent {scroll.Extent.Height}");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /** 只跑布局与派发队列(含比 Default 更低的优先级: 位置还原与该轮贴底都投递在上面), 不抓帧到底只抓一次。 */
    private static void Render(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
    }
}