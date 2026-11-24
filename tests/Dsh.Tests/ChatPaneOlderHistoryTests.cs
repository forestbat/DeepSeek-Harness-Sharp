using Dsh.Agent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
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

        // 滚到顶: 自动接上更早的一页(不再有"更早的 N 条事件未载入内存"那行提示)。
        var before = pane.Renderer.UserMessageCount;
        pane.ScrollOffset = 0;
        pane.HandleMouseWheel(120);
        Assert.True(pane.Renderer.UserMessageCount > before, "scrolling to the top must load earlier messages");
        Assert.DoesNotContain(pane.SnapshotLines(), line => line.Contains("未载入内存", StringComparison.Ordinal));

        // 一直滚到顶: 历史补到 seq 0 为止, 最早那条消息因此可以跳过去。
        for (var attempt = 0; attempt < 60 && pane.HasOlderHistory; attempt += 1)
        {
            pane.ScrollOffset = 0;
            pane.HandleMouseWheel(120);
        }
        Assert.False(pane.HasOlderHistory);
        Draw(chat);
        Assert.True(pane.TryScrollToUserMessage(0, total), "the oldest user message must be jumpable once loaded");
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