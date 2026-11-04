using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Tui;
using Dsh.Tui.Services;

namespace Dsh.Tests;

/**
 * TUI 状态行必须显示会话的上下文占用: 请求上下文给容量, 用量给占用, 取最近一次请求。
 * 状态行同时承载就绪提示与菜单提示, 所以还要确认上下文不会把它们挤掉。
 */
[Collection("TuiTheme")]
public sealed class ChatPaneContextStatusTests : IDisposable
{
    private readonly string _homeDir = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/test-homes")),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Status_ShowsContextOccupancy_AfterRequestAndUsage()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);

        agent.Session.Append(new RequestContextPayload("deepseek-official", "deepseek-v4-flash", 128_000));
        agent.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.Usage(new TokenUsage(12_800, 400))));
        chat.DrainUi();

        Assert.StartsWith("上下文 12.8k/128.0k (10%)", chat.InputPane.StatusText, StringComparison.Ordinal);
    }

    /** 上下文占用只是前缀, 就绪提示必须仍在, 否则用户看不到按键说明。 */
    [Fact]
    public async Task Status_KeepsReadyHint_AfterContextArrives()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);

        agent.Session.Append(new RequestContextPayload("deepseek-official", "deepseek-v4-flash", 128_000));
        agent.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.Usage(new TokenUsage(3_200, 10))));
        chat.DrainUi();

        var status = chat.InputPane.StatusText;
        Assert.StartsWith("上下文 3.2k/128.0k", status, StringComparison.Ordinal);
        Assert.Contains("ready - Enter to send", status, StringComparison.Ordinal);
    }

    /** 窗口容量未知时只报占用量, 不能编造百分比。 */
    [Fact]
    public async Task Status_OmitsPercentage_WhenContextWindowMissing()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);

        agent.Session.Append(new RequestContextPayload("deepseek-official", "unknown-model", null));
        agent.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.Usage(new TokenUsage(7_200, 10))));
        chat.DrainUi();

        var status = chat.InputPane.StatusText;
        Assert.StartsWith("上下文 7.2k", status, StringComparison.Ordinal);
        Assert.DoesNotContain("%", status, StringComparison.Ordinal);
    }

    /** 没有任何用量时状态行保持原样, 不出现占位符噪音。 */
    [Fact]
    public async Task Status_Unchanged_WithoutUsage()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);

        Assert.DoesNotContain("上下文", chat.InputPane.StatusText, StringComparison.Ordinal);
    }

    /** 最终回答的用量也要计入: 有些适配器只在 assistant/message 里给 usage, 不走流式。 */
    [Fact]
    public async Task Status_AlsoUsesUsageFromAssistantMessage()
    {
        var (ctx, agent, home) = await CreateAgent();
        using var chat = new ChatWindow(ctx, agent, home);

        agent.Session.Append(new RequestContextPayload("deepseek-official", "deepseek-v4-flash", 64_000));
        agent.Session.Append(
            new AssistantMessagePayload(
                1, 1,
                MessageFactory.CreateAssistantMessage(
                    [new TextBlock("回答")], "deepseek-official", "deepseek-v4-flash"),
                new TokenUsage(4_800, 20)),
            new SurfaceOp.Append());
        chat.DrainUi();

        Assert.StartsWith("上下文 4.8k/64.0k", chat.InputPane.StatusText, StringComparison.Ordinal);
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
