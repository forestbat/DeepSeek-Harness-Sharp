using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Core;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.Llm;

namespace Dsh.Tests;

/**
 * 打开长会话时的落点: 第一帧就该在底部, 之后不许"先离开底部再回来"。
 *
 * 断言的量是**每一帧的视口状态序列**, 不是最终落点: 用户报的正是"向上加载一段时间 + 向下加载一段时间",
 * 只看最后一帧的话这条链路永远是对的。
 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class GuiSessionOpenJitterTests
{
    private const int Turns = 40;
    /** 分页到达用例: 首屏页有多少条用户消息(每条 10 行), 后续历史分成几段、每段几条到达。 */
    private const int PageTurns = 20;
    /** 后续历史到达的段数: 真实长会话有几十页, 这里够跨三页以上。 */
    private const int PageChunks = 3;
    /** 每段到达的条数: 按一页快照的量级, 单段就把 extent 撑高几万像素。 */
    private const int ChunkTurns = 240;
    /** 订阅流那一段一段到达的条数: 大段之间还有一串小段, 每段都独立触发一轮布局。 */
    private const int StreamTurns = 8;
    private const int StreamChunks = 6;
    private const double WindowWidth = 900;
    private const double WindowHeight = 520;
    private const double StickyTolerance = 24;
    /** 面板按已实体化项重估行高、布局取整都以 0.5px 为步长: 一两像素的位移不是"离开底部"。 */
    private const double FrameTolerance = 2;

    /** 直接打开一条长会话: 第一帧就在底部, 之后每一帧都在底部, 偏移量单调不减(不许先离开再回来)。 */
    [Fact]
    public Task OpeningALongSession_LandsAtTheBottomOnTheFirstFrameAndStays() => HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        AppendHistory(environment.Agent.Session, Turns);
        var chat = await environment.OpenChatAsync();
        var rebuilds = 0;
        var stacks = new List<string>();
        chat.Reset += () =>
        {
            rebuilds++;
            stacks.Add(string.Join(" <- ", Environment.StackTrace
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("Dsh.", StringComparison.Ordinal) && !line.Contains("Dsh.Tests", StringComparison.Ordinal))
                .Take(6)
                .Select(line => line.Trim())));
        };
        await using var viewModel = new MainViewModel(environment.App, chat);
        var view = new ChatView { DataContext = viewModel };
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };
        window.Show();
        try
        {
            var frames = PumpFrames(window, view, 8);
            Assert.True(frames[^1].Extent > frames[^1].Viewport,
                "precondition: the transcript must overflow the viewport");
            Report("open", frames, rebuilds);
            TestContext.Current.SendDiagnosticMessage($"open resets: [{string.Join(" || ", stacks)}]");
            AssertFirstFrameAtBottom(frames);
            AssertNeverLeavesTheBottom(frames);
            Assert.True(rebuilds <= 1, $"opening a session must rebuild the transcript at most once, saw {rebuilds}");
            Assert.Equal(Turns, viewModel.Messages.Count(message => message.Kind == MessageKind.User));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /**
     * 从另一条(空的)会话切换回长会话: 这才是用户报的路径。
     * 视口必须从第一帧起就贴在长会话的末尾, 不许先落在会话头再被一段段拉下去。
     */
    [Fact]
    public Task SwitchingToALongSession_LandsAtTheBottomOnTheFirstFrameAndStays() => HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        AppendHistory(environment.Agent.Session, Turns);
        var chat = await environment.OpenChatAsync();
        var rebuilds = 0;
        var stacks = new List<string>();
        chat.Reset += () =>
        {
            rebuilds++;
            stacks.Add(string.Join(" <- ", Environment.StackTrace
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("Dsh.", StringComparison.Ordinal) && !line.Contains("Dsh.Tests", StringComparison.Ordinal))
                .Take(4)
                .Select(line => line.Trim())));
        };
        await using var viewModel = new MainViewModel(environment.App, chat);
        var view = new ChatView { DataContext = viewModel };
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };
        window.Show();
        try
        {
            var longSession = viewModel.SelectedSession!;
            var before = PumpFrames(window, view, 4);
            Assert.True(before[^1].AtBottom, $"precondition: the long session opens at its tail ({Describe(before)})");

            // 切到一条新建(空)会话: 这条会话的视口只有 ON, 长会话的像素不能留在这里冒充"阅读位置"。
            viewModel.NewSessionCommand.Execute(null);
            await GuiTestEnvironment.WaitForAsync(viewModel,
                () => viewModel.SelectedSession is { } selected && selected.SessionId != longSession.SessionId);
            PumpFrames(window, view, 4);

            var frames = new List<Frame>();
            rebuilds = 0;
            stacks.Clear();
            viewModel.SelectedSession = longSession;
            await GuiTestEnvironment.WaitForAsync(viewModel, () => viewModel.Messages.Count == Turns);
            frames.AddRange(PumpFrames(window, view, 8));

            Assert.True(frames[^1].Extent > frames[^1].Viewport,
                "precondition: the long transcript must overflow the viewport");
            Report("open", frames, rebuilds);
            AssertFirstFrameAtBottom(frames);
            AssertNeverLeavesTheBottom(frames);
            Assert.True(rebuilds <= 1, $"switching to a session must rebuild the transcript at most once, saw {rebuilds} [{string.Join(" || ", stacks)}]");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /**
     * 记下的阅读位置在新窗口下已经不存在(窗口变大 → 最大偏移变小, 或者会话被截短):
     * 还原落不下去时不许停在会话头, 必须落到末尾 —— 这就是用户看到的"先跑偏"。
     */
    [Fact]
    public Task ReopeningALongSessionWithAStaleReadingPosition_LandsAtTheBottom() => HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        AppendHistory(environment.Agent.Session, Turns);
        var chat = await environment.OpenChatAsync();
        await using var viewModel = new MainViewModel(environment.App, chat);
        var view = new ChatView { DataContext = viewModel };
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };
        window.Show();
        try
        {
            var longSession = viewModel.SelectedSession!;
            var scroll = Assert.IsType<ScrollViewer>(Assert.IsType<ListBox>(view.FindControl<ListBox>("MessageList")!).Scroll);
            PumpFrames(window, view, 3);
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
            PumpFrames(window, view, 2);
            var remembered = scroll.Offset.Y;
            Assert.True(remembered > 0, "precondition: the reading position must be a real offset");

            viewModel.NewSessionCommand.Execute(null);
            await GuiTestEnvironment.WaitForAsync(viewModel,
                () => viewModel.SelectedSession is { } selected && selected.SessionId != longSession.SessionId);
            PumpFrames(window, view, 3);

            // 窗口变高: 同样的内容装得下更多, 最大偏移比记下的那个小, 那个像素在新窗口里根本不存在。
            window.Height = WindowHeight * 2;
            viewModel.SelectedSession = longSession;
            await GuiTestEnvironment.WaitForAsync(viewModel, () => viewModel.Messages.Count == Turns);
            var frames = PumpFrames(window, view, 6);

            Assert.True(frames[^1].Extent > frames[^1].Viewport,
                "precondition: the long transcript must overflow the viewport");
            Report("reopen with a stale position", frames, rebuilds: 0);
            AssertFirstFrameAtBottom(frames);
            AssertNeverLeavesTheBottom(frames);
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /**
     * 打开一条**历史分成好几段到达**的会话: 就像真实长会话那样, 首屏只拿到一段快照,
     * 其余历史随后按订阅流一段段到齐, 每段到齐都往转录尾部追加。窗口与消息体量让转录远超一屏,
     * 于是每一段都改变 extent。
     *
     * 断言: 用户没碰视口时每一帧都在底部(1px 容差), 偏移量单调不减(不许"先减小再增大"的往返),
     * 且全程跟随意图不被这些追加改判。
     */
    [Fact]
    public Task OpeningASessionWhoseHistoryArrivesInPages_StaysAtTheBottom() => HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        AppendHistory(session, PageTurns, varied: true);
        var chat = await environment.OpenChatAsync();
        var resets = new List<string>();
        chat.Reset += () => resets.Add(string.Join(" <- ", Environment.StackTrace
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("Dsh.", StringComparison.Ordinal) && !line.Contains("Dsh.Tests", StringComparison.Ordinal))
            .Take(5)
            .Select(line => line.Trim())));
        await using var viewModel = new MainViewModel(environment.App, chat);
        var view = new ChatView { DataContext = viewModel };
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };
        window.Show();
        try
        {
            var scroll = Assert.IsType<ScrollViewer>(Assert.IsType<ListBox>(view.FindControl<ListBox>("MessageList")!).Scroll);
            var trace = new List<string>();
            ListOf(view).AddHandler(ScrollViewer.ScrollChangedEvent, (_, args) =>
            {
                var change = Assert.IsType<ScrollChangedEventArgs>(args);
                var maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                var gap = maximum - scroll.Offset.Y;
                if (change.OffsetDelta.Y >= -1 && gap <= 1)
                    return;
                trace.Add($"offset{change.OffsetDelta.Y:+0;-0;0}/extent{change.ExtentDelta.Y:+0;-0;0}"
                    + $" → {scroll.Offset.Y:0} (底部 {@maximum:0}, 差 {gap:0}){(IsSticky(view) ? "" : "!跟随已断")}");
            });
            var frames = PumpFrames(window, view, 3);
            var start = PageTurns;
            for (var chunk = 0; chunk < PageChunks; chunk++)
            {
                var expected = viewModel.Messages.Count + ChunkTurns;
                AppendHistory(session, ChunkTurns, start, varied: true);
                start += ChunkTurns;
                await GuiTestEnvironment.WaitForAsync(viewModel.Messages, () => viewModel.Messages.Count >= expected);
                frames.AddRange(PumpFrames(window, view, 1, frames.Count));
            }
            for (var chunk = 0; chunk < StreamChunks; chunk++)
            {
                var expected = viewModel.Messages.Count + StreamTurns;
                AppendHistory(session, StreamTurns, start, varied: true);
                start += StreamTurns;
                await GuiTestEnvironment.WaitForAsync(viewModel.Messages, () => viewModel.Messages.Count >= expected);
                frames.AddRange(PumpFrames(window, view, 1, frames.Count));
            }

            Assert.True(frames[^1].Extent > frames[^1].Viewport,
                "precondition: the transcript must overflow the viewport");
            Report("open in pages", frames, rebuilds: 0);
            TestContext.Current.SendDiagnosticMessage(
                $"scroll events off the bottom: {trace.Count} [{string.Join(" | ", trace.Take(20))}]; "
                + $"resets: {resets.Count} [{string.Join(" || ", resets.Take(2))}]");
            AssertFirstFrameAtBottom(frames);
            AssertNeverLeavesTheBottom(frames);
            AssertPinnedToTheBottom(frames);
            Assert.True(frames.All(frame => frame.Sticky),
                $"appending history must never change the follow intent ({Describe(frames)})");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /** 用户主动上滚之后, 后续到达的历史段不许把视口拽回底部。 */
    [Fact]
    public Task ScrollingUpThenHistoryArrivingInPages_DoesNotSnapBackToTheBottom() => HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        AppendHistory(session, PageTurns);
        var chat = await environment.OpenChatAsync();
        await using var viewModel = new MainViewModel(environment.App, chat);
        var view = new ChatView { DataContext = viewModel };
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };
        window.Show();
        try
        {
            var scroll = Assert.IsType<ScrollViewer>(Assert.IsType<ListBox>(view.FindControl<ListBox>("MessageList")!).Scroll);
            var trace = new List<string>();
            ListOf(view).AddHandler(ScrollViewer.ScrollChangedEvent, (_, args) =>
            {
                var change = Assert.IsType<ScrollChangedEventArgs>(args);
                var maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                trace.Add($"offset{change.OffsetDelta.Y:+0;-0;0}/extent{change.ExtentDelta.Y:+0;-0;0}"
                    + $" → {scroll.Offset.Y:0} (max {maximum:0}){(IsSticky(view) ? "" : "!跟随已断")}");
            });
            PumpFrames(window, view, 3);
            for (var attempt = 0; attempt < 12 && !IsAwayFromTheTail(scroll); attempt++)
            {
                var before = scroll.Offset.Y;
                Wheel(window, scroll, 6);
                trace.Add($"wheel {before:0}→{scroll.Offset.Y:0} sticky={(IsSticky(view) ? 1 : 0)}");
                PumpFrames(window, view, 1);
                trace.Add($"pump → {scroll.Offset.Y:0} sticky={(IsSticky(view) ? 1 : 0)}");
            }
            Assert.True(IsAwayFromTheTail(scroll), "precondition: the reader must be away from the tail");
            var readingPosition = scroll.Offset.Y;
            Assert.False(IsSticky(view),
                $"precondition: a wheel scroll away from the tail must end the follow intent (offset {readingPosition:0}); "
                + $"events: [{string.Join(" | ", trace)}]");
            trace.Clear();

            var start = PageTurns;
            for (var page = 0; page < PageChunks; page++)
            {
                var expected = viewModel.Messages.Count + ChunkTurns;
                AppendHistory(session, ChunkTurns, start);
                start += ChunkTurns;
                await GuiTestEnvironment.WaitForAsync(viewModel.Messages, () => viewModel.Messages.Count >= expected);
                PumpFrames(window, view, 2);
                Assert.True(Math.Abs(scroll.Offset.Y - readingPosition) <= FrameTolerance,
                    $"page {page} arriving must not move the viewport the reader is in "
                    + $"({readingPosition:0} → {scroll.Offset.Y:0}, max {scroll.Extent.Height - scroll.Viewport.Height:0}); "
                    + $"events: [{string.Join(" | ", trace.Take(12))}]");
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /**
     * 回退(只追加一条 session/revert)不许整屏重建, 落点按锚点决定:
     * 锚点还在(回退点在被看那条之后) → 用户正在读的内容一动不动; 锚点没了 → 落到末尾。
     */
    [Fact]
    public Task RevertingASession_KeepsTheAnchorOrLandsAtTheBottom() => HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        AppendHistory(session, Turns);
        var chat = await environment.OpenChatAsync();
        var rebuilds = 0;
        chat.Reset += () => rebuilds++;
        await using var viewModel = new MainViewModel(environment.App, chat);
        var view = new ChatView { DataContext = viewModel };
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };
        window.Show();
        try
        {
            var scroll = Assert.IsType<ScrollViewer>(Assert.IsType<ListBox>(view.FindControl<ListBox>("MessageList")!).Scroll);
            PumpFrames(window, view, 3);
            for (var attempt = 0; attempt < 12 && !IsAwayFromTheTail(scroll); attempt++)
            {
                Wheel(window, scroll, 6);
                PumpFrames(window, view, 1);
            }
            Assert.True(IsAwayFromTheTail(scroll), "precondition: the reader must be away from the tail");

            // 锚点 = 视口顶部那条消息(这份历史只有用户消息, 每条都带事件序号)。
            var anchor = TopVisible(view, scroll);
            Assert.Equal(MessageKind.User, anchor.Kind);
            var anchorTop = TopOf(view, scroll, anchor)!.Value;
            var keep = anchor.Seq!.Value + 1;
            var before = viewModel.Messages.Count;
            Assert.True(keep < before, $"precondition: the revert must discard something (keep {keep} of {before})");

            rebuilds = 0;
            Assert.NotNull(session.Revert(keep));
            await GuiTestEnvironment.WaitForAsync(viewModel.Messages, () => viewModel.Messages.Count < before);
            PumpFrames(window, view, 4);

            Assert.Equal(0, rebuilds);
            var kept = viewModel.Messages.FirstOrDefault(message => message.Text == anchor.Text && message.Kind == MessageKind.User);
            Assert.NotNull(kept);
            var keptTop = TopOf(view, scroll, kept);
            Assert.True(keptTop is not null && Math.Abs(keptTop.Value - anchorTop) <= FrameTolerance,
                $"the message being read must not move on a revert that keeps it ({anchorTop:0} → {keptTop:0})");
            Assert.True(!IsAtBottom(scroll), "a kept anchor must not be dragged to the bottom");

            // 再回退到锚点之前: 用户正在看的那条已经不存在, 这时才贴底。
            var count = viewModel.Messages.Count;
            Assert.NotNull(session.Revert(anchor.Seq!.Value));
            await GuiTestEnvironment.WaitForAsync(viewModel.Messages, () => viewModel.Messages.Count < count);
            var frames = PumpFrames(window, view, 4);
            Assert.Equal(0, rebuilds);
            Assert.True(IsAtBottom(scroll), $"a revert that removes the anchor lands at the bottom ({Describe(frames)})");
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /** 每条历史消息的行数: 固定值让转录"均高", 变高变矮才是真实会话(面板对未实体化项的估算会偏)。 */
    private const int HistoryLines = 10;

    private static void AppendHistory(Session session, int turns, int start = 0, bool varied = false)
    {
        for (var index = start; index < start + turns; index++)
            session.Append(
                new UserMessagePayload(MessageFactory.CreateUserText(History(index, varied ? LinesOf(index) : HistoryLines))),
                new SurfaceOp.Append());
    }

    /** 1..60 行轮流出现: 行高差几十倍, 面板的"平均行高"估算因此必然不准。 */
    private static int LinesOf(int index) => 1 + (index * 7) % 60;

    private static string History(int index, int lines)
        => string.Join('\n', Enumerable.Range(0, lines).Select(line => $"历史 {index} 第 {line} 行: 用来把转录撑到远超一屏。"));

    private static ListBox ListOf(ChatView view) => Assert.IsType<ListBox>(view.FindControl<ListBox>("MessageList")!);

    private static bool IsAtBottom(ScrollViewer scroll)
        => scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - StickyTolerance;

    private static bool IsAwayFromTheTail(ScrollViewer scroll)
        => scroll.Offset.Y < scroll.Extent.Height - scroll.Viewport.Height - 40;

    private static void Wheel(Window window, ScrollViewer scroll, double delta)
    {
        var point = scroll.TranslatePoint(new Point(scroll.Bounds.Width / 2, scroll.Viewport.Height / 2), window)!.Value;
        window.MouseWheel(point, new Vector(0, delta));
    }

    private static double? TopOf(ChatView view, ScrollViewer scroll, MessageViewModel message)
        => ListOf(view).GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(item => ReferenceEquals(item.DataContext, message))
            ?.TranslatePoint(default, scroll)?.Y;

    /** 视口顶部那条消息(容器已实体化、屏幕位置在视口上沿之下)。 */
    private static MessageViewModel TopVisible(ChatView view, ScrollViewer scroll)
        => Assert.IsType<MessageViewModel>(ListOf(view).GetVisualDescendants().OfType<ListBoxItem>()
            .Select(item => (Item: item, Top: item.TranslatePoint(default, scroll)?.Y ?? double.MaxValue))
            .Where(candidate => candidate.Top > -0.5 && candidate.Item.Bounds.Height > 0)
            .OrderBy(candidate => candidate.Top)
            .First().Item.DataContext);

    private static List<Frame> PumpFrames(Window window, ChatView view, int passes, int firstIndex = 0)
    {
        var scroll = Assert.IsType<ScrollViewer>(Assert.IsType<ListBox>(view.FindControl<ListBox>("MessageList")!).Scroll);
        var frames = new List<Frame>();
        for (var pass = 0; pass < passes; pass++)
        {
            Render(window);
            frames.Add(new Frame(firstIndex + pass, scroll.Offset.Y, scroll.Viewport.Height, scroll.Extent.Height, IsSticky(view)));
        }
        return frames;
    }

    /** 视图的跟随意图: 分页到达期间它不该被内容增长改判。 */
    private static bool IsSticky(ChatView view)
        => (bool)(typeof(ChatView)
            .GetField("_sticky", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(view) ?? true);

    /** 第一帧就必须在底部: 用户打开会话时看到的第一屏就是最新内容。 */
    private static void AssertFirstFrameAtBottom(IReadOnlyList<Frame> frames)
    {
        var first = frames.FirstOrDefault(frame => frame.Extent > frame.Viewport);
        Assert.True(first is { AtBottom: true },
            $"the first frame of a freshly opened session must sit at the bottom ({Describe(frames)})");
    }

    /** 一旦落到过底部, 之后每一帧都得在底部: 任何"离开再回来"都是用户看到的抖动。 */
    private static void AssertNeverLeavesTheBottom(IReadOnlyList<Frame> frames)
    {
        var landed = false;
        var previous = frames[0];
        foreach (var frame in frames)
        {
            if (!landed && frame.AtBottom)
                landed = true;
            else if (landed && !frame.AtBottom)
                Assert.Fail($"the viewport left the bottom at frame {frame.Index} and had to come back ({Describe(frames)})");
            if (frame.Offset < previous.Offset - FrameTolerance)
                Assert.Fail($"the viewport moved up at frame {frame.Index}: {previous.Offset:0} → {frame.Offset:0} ({Describe(frames)})");
            previous = frame;
        }
    }

    /** 用户没碰过视口时, 每一帧都必须正好贴在底部(1px 容差): 差一像素就是"还在往底部追"。 */
    private static void AssertPinnedToTheBottom(IReadOnlyList<Frame> frames)
    {
        foreach (var frame in frames)
        {
            if (frame.Extent <= frame.Viewport)
                continue;
            var maximum = frame.Extent - frame.Viewport;
            Assert.True(Math.Abs(frame.Offset - maximum) <= 1,
                $"frame {frame.Index} is {maximum - frame.Offset:0}px above the bottom ({Describe(frames)})");
        }
    }

    private static string Describe(IReadOnlyList<Frame> frames)
        => "frames: " + string.Join(", ", frames.Select(frame =>
            $"#{frame.Index} {frame.Offset:0}/{(frame.Extent - frame.Viewport < 0 ? 0 : frame.Extent - frame.Viewport):0}"
            + $"{(frame.AtBottom ? "*" : "")}{(frame.Sticky ? "" : "!跟随已断")}"));

    /** 取证: 每一帧的落点与重建次数都进诊断输出(报告与回归对比都用这一行)。 */
    private static void Report(string label, IReadOnlyList<Frame> frames, int rebuilds)
        => TestContext.Current.SendDiagnosticMessage($"{label}: {Describe(frames)}; rebuilds={rebuilds}");

    private readonly record struct Frame(int Index, double Offset, double Viewport, double Extent, bool Sticky = true)
    {
        public bool AtBottom => Extent > 0.5 && Offset + Viewport >= Extent - StickyTolerance;
    }

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
