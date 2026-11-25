using System.Text;
using Dsh.Agent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Persistence;
using Dsh.Llm;
using Dsh.Tui;

namespace Dsh.Tests;

/**
 * 窗口化恢复的会话: 更早的历史不在转录里, 但用户往上滚或跳转时必须能拿到。
 *
 * 以前的实现只在顶端留一行"更早的 N 条事件未载入内存", 而 /timestamp jump 也只能跳到已经渲染出来的消息上 ——
 * 用户当然会往上滚、也会跳到很早以前的消息, 所以这里直接钉住这两个入口。
 */
[Collection("TuiTheme")]
public sealed class ChatPaneOlderHistoryTests : IDisposable
{
    private const int EventCount = 60_000;

    [Fact]
    public async Task ScrollingToTop_LoadsEarlierHistory_AndOldMessagesBecomeJumpable()
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var session = environment.Agent.Session;
        var sessionId = session.Id;
        var options = new AgentOptions("test", "test-model");
        var turn = 1;
        while (session.Seq < EventCount)
        {
            session.Append(new TurnStartPayload(turn));
            session.Append(new StepStartPayload(turn, 1));
            session.Append(new AssistantChunkPayload(turn, 1, new StreamChunk.TextDelta(0, $"chunk {session.Seq} 行内容\n")));
            session.Append(new UserMessagePayload(MessageFactory.CreateUserText($"q{turn}")), new SurfaceOp.Append());
            turn += 1;
        }
        // 窗口化恢复靠折叠检查点: 先折到末尾, 关闭会话时持久化才会写下一份(否则 Resume 只能整份恢复)。
        session.AssertFullyFolded();
        await environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(session);

        await using var restarted = await environment.RestartAsync();
        var agents = restarted.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Resume(new ResumeAgentOptions(sessionId, options), TestContext.Current.CancellationToken);
        var agent = (AgentLoopAgent)handle.Agent;
        Assert.True(agent.Session.ResidentFrom > 0,
            $"precondition: the session must resume windowed, residentFrom={agent.Session.ResidentFrom}");

        using var chat = new ChatWindow(restarted.App.Ctx, agent, restarted.App.Home);
        var pane = chat.InputPane;
        var total = SessionTimeline.UserMessages(agent.Session).Count;
        Assert.True(pane.HasOlderHistory, "the transcript must start on the resident tail");
        // 画一帧: 跳转依赖绘制时建好的视口缓存。最早那条用户消息还没渲染, 所以现在跳不过去。
        Draw(chat);
        Assert.False(pane.TryScrollToUserMessage(0, total));

        // 视口停在正文中间时补页: 顶行必须一动不动(T-1: 旧实现把字符差加到行号上, 补完被钳到末尾)。
        pane.ScrollOffset = 500;
        Draw(chat);
        var midTop = pane.ViewportTopText();
        var lengthBefore = pane.Renderer.CurrentLength;
        Assert.True(pane.LoadOlderHistory(), "there must be an earlier page to load");
        Draw(chat);
        Assert.False(pane.StickToBottom, "loading older history must not resume bottom following");
        Assert.Equal(midTop, pane.ViewportTopText());
        Assert.True(pane.Renderer.CurrentLength > lengthBefore, "补页必须把更早的内容接上来");
        Assert.Null(pane.OlderHistoryStatusText);

        // 一直滚到顶: 历史补到 seq 0 为止, 期间任何一次补页都不许把视口甩到底部。
        for (var attempt = 0; attempt < 60 && pane.HasOlderHistory; attempt += 1)
        {
            pane.ScrollOffset = 0;
            Draw(chat);
            pane.HandleMouseWheel(120);
            Draw(chat);
            Assert.False(pane.StickToBottom, "loading older history must not resume bottom following");
        }
        Assert.False(pane.HasOlderHistory);
        // 不再有"更早的 N 条事件未载入内存"那行提示: 缺口要么不存在, 要么有明确原因。
        Assert.DoesNotContain(pane.SnapshotLines(), line => line.Contains("未载入内存", StringComparison.Ordinal));
        Assert.Null(pane.OlderHistoryStatusText);
        Draw(chat);
        // 整段加载完之后, 转录里的用户消息条数必须与日志一致(中间不许少一段)。
        Assert.Equal(total, pane.Renderer.UserMessageCount);
        Assert.True(pane.TryScrollToUserMessage(0, total), "the oldest user message must be jumpable once loaded");
    }

    /**
     * 一页一页往前接, 画出来必须和"一次接完整段"逐字符相同。
     *
     * 补页只重折新接的那一页, 既有行整体后移(见 ChatPane.PrependWrapCache): 那份平移只要错一格,
     * 视口就会整体跑偏一截 —— 而这种错位在"能跳转、也能到顶"的粗测里看不出来。
     * 所以这里拿"整段一次折完"当基准, 对每一屏做逐字符比对。
     */
    [Fact]
    public async Task PrependPageByPage_RendersExactlyLikeLoadingEverythingAtOnce()
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var options = new AgentOptions("test", "test-model");
        SeedEvents(environment.Agent.Session, EventCount);
        var session = environment.Agent.Session;
        session.AssertFullyFolded();
        await environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(session);
        await using var restarted = await environment.RestartAsync();
        var agents = restarted.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Resume(new ResumeAgentOptions(session.Id, options), TestContext.Current.CancellationToken);
        var agent = (AgentLoopAgent)handle.Agent;

        using var oneShot = new ChatWindow(restarted.App.Ctx, agent, restarted.App.Home);
        _ = RenderText(oneShot);
        Assert.True(oneShot.InputPane.LoadOlderHistoryTo(0), "一次接完整段必须成功");
        var expected = RenderText(oneShot);

        using var pageByPage = new ChatWindow(restarted.App.Ctx, agent, restarted.App.Home);
        _ = RenderText(pageByPage);
        var pane = pageByPage.InputPane;
        for (var attempt = 0; attempt < 60 && pane.HasOlderHistory; attempt += 1)
        {
            pane.ScrollOffset = 0;
            _ = RenderText(pageByPage);
            Assert.True(pane.LoadOlderHistory(), $"第 {attempt + 1} 次补页应当还有更早的历史");
        }
        Assert.False(pane.HasOlderHistory);
        Assert.Equal(oneShot.InputPane.SnapshotLines(), pane.SnapshotLines());

        // 每一屏都要对上: 折行几何不同的话, 相同的 ScrollOffset 会停在不同的行上。
        // 两个窗格各自停在哪儿不重要(锚点还原会保留各自的阅读位置), 所以先钉成同样的滚动位置再比。
        oneShot.InputPane.StickToBottom = false;
        pane.StickToBottom = false;
        for (var offset = 0; offset < 60_000; offset += 137)
        {
            oneShot.InputPane.ScrollOffset = offset;
            pane.ScrollOffset = offset;
            Assert.Equal(RenderText(oneShot), RenderText(pageByPage));
        }
    }

    /** 会话里塞满"回合 + 助手输出 + 用户消息", 供窗口化恢复用。 */
    private static void SeedEvents(Session session, int eventCount)
    {
        var turn = 1;
        while (session.Seq < eventCount)
        {
            session.Append(new TurnStartPayload(turn));
            session.Append(new StepStartPayload(turn, 1));
            // 每 10 个回合带一段代码围栏: 折叠会改变折行几何(折叠头 + 展开体), 平移折行缓存时最容易在这里错位。
            var fence = turn % 10 == 0 ? $"\n```\n代码 {turn}\n```" : "";
            session.Append(new AssistantChunkPayload(turn, 1, new StreamChunk.TextDelta(0, $"chunk {session.Seq} 行内容{fence}\n")));
            session.Append(new UserMessagePayload(MessageFactory.CreateUserText($"q{turn}")), new SurfaceOp.Append());
            turn += 1;
        }
    }

    /** 画一帧并把 30×100 个格子读回来: 折行是否等价, 只有逐字符比才看得见。 */
    private static string RenderText(ChatWindow chat)
    {
        chat.DrainUi();
        var grid = new CellGrid(100, 30);
        chat.Draw(grid, LayoutEngine.Calculate(100, 30));
        var text = new StringBuilder();
        for (var row = 0; row < grid.Height; row += 1)
        {
            foreach (var cell in grid.Row(row))
                _ = text.Append(cell.Character);
            _ = text.Append('\n');
        }
        return text.ToString();
    }

    private static void Draw(ChatWindow chat)
    {
        chat.DrainUi();
        var grid = new CellGrid(100, 30);
        chat.Draw(grid, LayoutEngine.Calculate(100, 30));
    }
    public void Dispose()
    {
    }
}






