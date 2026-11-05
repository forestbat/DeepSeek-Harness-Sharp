using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tests;

/** 最近会话的排序只认"用户自己用过这个会话": LLM 的输出与工具事件不得刷新活动时间。 */
public sealed class HarnessSessionActivityTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task SessionActivity_MovesOnlyForUserMessages()
    {
        await using var host = await HarnessHostFixture.CreateAsync("act", Ct);
        var client = await host.ConnectAsync("client", Ct);
        var created = await client.CreateSessionAsync(host.Environment.App.Home.Root, Ct);
        var agent = RemoteHostMultiClientTests.Agent(host, created.Id);
        var registry = host.Backend.Sessions;
        var start = registry.Get(created.Id)!.LastActivityAt;

        // 模型输出与工具调用: 会话在"工作", 但不是"被用户使用"。
        agent.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "模型正在输出")));
        agent.Session.Append(new ToolCallPayload(1, 1, new ToolCallId("call-1"), "bash", "{}"));
        Assert.Equal(start, registry.Get(created.Id)!.LastActivityAt);

        // 用户发消息: 这才算用过。
        agent.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("再问一句")), new SurfaceOp.Append());
        Assert.True(registry.Get(created.Id)!.LastActivityAt > start,
            "a user message must count as the session being used");
    }
}
