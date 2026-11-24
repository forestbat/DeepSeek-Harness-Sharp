using Dsh.Agent;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tests;

/**
 * 请求投影的配对不变量: 发给供应商的消息里, assistant 的每个 tool_calls 后面必须跟齐 tool 结果。
 *
 * 这条兜底不依赖"历史为什么缺": 停写的尾巴、取不回的页、回退或压缩切在配对中间都会走到同一个地方。
 * 缺的补成"结果未知"(与恢复时的收尾文本同一句), 孤立的 tool 结果丢掉。
 */
public sealed class ToolCallPairingTests
{
    [Fact]
    public void MissingResultForOneOfTwoCalls_IsPatched()
    {
        var callA = ToolCallId.Create("call_a");
        var callB = ToolCallId.Create("call_b");
        var messages = new Message[]
        {
            User("继续"),
            Assistant(Call(callA), Call(callB)),
            Result(callA),
            User("下一条"),
        };

        var repaired = ToolCallPairing.Repair(messages);

        Assert.Equal(1, repaired.Patched);
        Assert.Equal(0, repaired.Dropped);
        Assert.Equal(5, repaired.Messages.Count);
        Assert.Equal(callB.Value, ((ToolResultBlock)repaired.Messages[3].Content[0]).ToolCallId.Value);
        Assert.True(((ToolResultBlock)repaired.Messages[3].Content[0]).IsError);
    }

    [Fact]
    public void MissingResultsAtTheTail_ArePatchedBeforeTheEnd()
    {
        var call = ToolCallId.Create("call_tail");
        var repaired = ToolCallPairing.Repair(new Message[] { User("继续"), Assistant(Call(call)) });

        Assert.Equal(1, repaired.Patched);
        Assert.Equal(3, repaired.Messages.Count);
        Assert.IsType<ToolResultBlock>(repaired.Messages[^1].Content[0]);
    }

    [Fact]
    public void OrphanToolResult_IsDropped()
    {
        var repaired = ToolCallPairing.Repair(new Message[] { User("继续"), Result(ToolCallId.Create("call_unknown")) });

        Assert.Equal(1, repaired.Dropped);
        Assert.Single(repaired.Messages);
    }

    [Fact]
    public void PairedHistory_IsLeftUntouched()
    {
        var call = ToolCallId.Create("call_ok");
        var messages = new Message[] { User("继续"), Assistant(Call(call)), Result(call), User("下一条") };

        var repaired = ToolCallPairing.Repair(messages);

        Assert.Equal(0, repaired.Patched);
        Assert.Equal(0, repaired.Dropped);
        Assert.Equal(messages.Length, repaired.Messages.Count);
    }

    /** 只补一条"结果未知", 并把该结果放在下一个非工具消息之前(供应商按顺序校验)。 */
    [Fact]
    public void PatchedResult_SitsImmediatelyAfterItsToolCalls()
    {
        var call = ToolCallId.Create("call_order");
        var repaired = ToolCallPairing.Repair(new Message[] { User("继续"), Assistant(Call(call)), User("下一条") });

        Assert.Equal(4, repaired.Messages.Count);
        Assert.IsType<ToolResultBlock>(repaired.Messages[2].Content[0]);
        Assert.Contains(
            SessionRepair.OutcomeUnknownText,
            ((ToolResultBlock)repaired.Messages[2].Content[0]).Content.OfType<TextBlock>().Select(block => block.Text));
    }

    private static UserMessage User(string text) => MessageFactory.CreateUserText(text);

    private static AssistantMessage Assistant(params ContentBlock[] content)
        => MessageFactory.CreateAssistantMessage(content, "test", "test-model");

    private static ToolCallBlock Call(ToolCallId callId) => new(callId, "read", "{}");

    private static ToolResultMessage Result(ToolCallId callId)
        => MessageFactory.CreateToolResultMessage(callId, [new TextBlock("ok")], isError: false);
}
