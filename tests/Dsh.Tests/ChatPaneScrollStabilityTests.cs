using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Tui;
using Dsh.Tui.Services;

namespace Dsh.Tests;

/**
 * 硬性原则: 输出(流式增量、新消息、系统/工具提示)不得改变滚动条位置。
 * 用户滚到中途后, 后续到达的内容只能长在下面, 不能把视口拉回底部。
 */
[Collection("TuiTheme")]
public sealed class ChatPaneScrollStabilityTests : IDisposable
{
    private readonly string _homeDir = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/test-homes")),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SystemText_DoesNotResumeBottomFollowing()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);
        var pane = chat.InputPane;
        pane.HandleMouseWheel(3);
        Assert.False(pane.StickToBottom);

        pane.AppendRaw("工具返回了一大段结果");
        Assert.False(pane.StickToBottom);
    }

    [Fact]
    public async Task StreamingAnswer_DoesNotResumeBottomFollowing()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);
        var pane = chat.InputPane;
        pane.HandleMouseWheel(3);
        Assert.False(pane.StickToBottom);

        for (var index = 0; index < 5; index++)
        {
            agent.Session.Append(new AssistantChunkPayload(1, index, new StreamChunk.TextDelta(0, $"流式输出第 {index} 段")));
            chat.DrainUi();
            Assert.False(pane.StickToBottom);
        }
    }

    /** 滚回底部后恢复跟随: 跟随是用户的决定, 用户回到末尾就该重新贴底。 */
    [Fact]
    public async Task ScrollingBackToBottom_ResumesFollowing()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);
        var pane = chat.InputPane;
        for (var index = 0; index < 40; index++)
            agent.Session.Append(new AssistantChunkPayload(1, index, new StreamChunk.TextDelta(0, $"第 {index} 行")));
        chat.DrainUi();

        pane.HandleMouseWheel(3);
        Draw(chat);
        Assert.False(pane.StickToBottom);

        for (var attempt = 0; attempt < 60 && !pane.StickToBottom; attempt++)
        {
            pane.HandleMouseWheel(-3);
            Draw(chat);
        }
        Assert.True(pane.StickToBottom);
    }

    /** PageDown 到底也算用户回到末尾, 与滚轮向下同一判定: 之后的新输出必须继续贴底。 */
    [Fact]
    public async Task PageDownToTail_ResumesFollowing()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);
        var pane = chat.InputPane;
        for (var index = 0; index < 60; index++)
            agent.Session.Append(new AssistantChunkPayload(1, index, new StreamChunk.TextDelta(0, $"第 {index} 行")));
        Draw(chat);

        pane.HandleMouseWheel(3);
        Draw(chat);
        Assert.False(pane.StickToBottom);

        for (var attempt = 0; attempt < 60 && !pane.StickToBottom; attempt++)
        {
            pane.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.PageDown, false, false, false));
            Draw(chat);
        }

        Assert.True(pane.StickToBottom);
    }

    /** jump 只影响它自己那一帧: 用户随后发消息必须重新贴底(旧实现会把抑制留到下一次发送)。 */
    [Fact]
    public async Task JumpThenSend_ResumesFollowing()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);
        var pane = chat.InputPane;
        for (var index = 0; index < 60; index++)
            agent.Session.Append(new AssistantChunkPayload(1, index, new StreamChunk.TextDelta(0, $"第 {index} 行")));
        Draw(chat);

        Assert.True(pane.TryScrollToMessage("第 0 行"));
        Draw(chat);
        Assert.False(pane.StickToBottom);

        pane.Input = "下一条";
        pane.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        Draw(chat);

        Assert.True(pane.StickToBottom);
    }

    /** 贴底时展开折叠: 那一帧不重新贴底, 否则被点的折叠头会被新高度顶走(GUI 的 _foldPending 同款)。 */
    [Fact]
    public async Task ExpandingFoldWhileAtTail_KeepsTheTranscriptInPlace()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);
        var pane = chat.InputPane;
        for (var index = 0; index < 40; index++)
            agent.Session.Append(new AssistantChunkPayload(1, index, new StreamChunk.TextDelta(0, $"第 {index} 行")));
        pane.AppendRaw(string.Join('\n', Enumerable.Range(0, 40).Select(index => $"工具输出第 {index} 行")));
        Draw(chat);
        Assert.True(pane.StickToBottom);

        var fold = Assert.Single(pane.Renderer.Folds);
        var before = pane.ScrollOffset;
        pane.ToggleFold(fold);
        Draw(chat);

        Assert.False(pane.StickToBottom);
        Assert.Equal(before, pane.ScrollOffset);
        Assert.True(pane.Renderer.Folds[0].Lines > 0);
    }

    private static void Draw(ChatWindow chat)
    {
        chat.DrainUi();
        var grid = new CellGrid(100, 30);
        chat.Draw(grid, LayoutEngine.Calculate(100, 30));
    }

    private async Task<(Context Ctx, AgentLoopAgent Agent, HarnessHome Home)> CreateAgent()
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        CommandsService.Register(ctx);
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid():N}"),
            null,
            new AgentOptions("deepseek-official", "deepseek-v4-flash")));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();
        return (ctx, agent, HarnessHome.Resolve(_homeDir));
    }

    public void Dispose() => TempTree.Delete(_homeDir);
}
