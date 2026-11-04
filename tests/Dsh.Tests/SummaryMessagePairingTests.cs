using Dsh.Compaction;
using Dsh.Llm;

namespace Dsh.Tests;

/**
 * 摘要请求的消息序列必须对 OpenAI 兼容 API 合法: 带 tool_calls 的 assistant 后面必须紧跟对应的 tool 消息。
 * 会话表面上的事件级配对平衡不保证派生出的消息序列合法, 因此摘要前统一拍平工具结构。
 * 这些用例锁住拍平规则, 并顺带保证工具信息不会在拍平时丢失。
 */
public sealed class SummaryMessagePairingTests
{
    private static AssistantMessage AssistantWithCall(string callId, string name, string arguments, string? text = null)
    {
        var content = new List<ContentBlock>();
        if (text is not null)
            content.Add(new TextBlock(text));
        content.Add(new ToolCallBlock(ToolCallId.Create(callId), name, arguments));
        return MessageFactory.CreateAssistantMessage(content, "deepseek-official", "deepseek-v4-flash");
    }

    private static ToolResultMessage ToolResult(string callId, string output, bool isError = false)
        => MessageFactory.CreateToolResultMessage(ToolCallId.Create(callId), [new TextBlock(output)], isError);

    /** 核心保证: 拍平后不再有任何 tool_calls 或 tool 角色消息, 服务端因此无从触发配对校验。 */
    [Fact]
    public void Flatten_LeavesNoToolCallsOrToolRoleMessages()
    {
        IReadOnlyList<Message> messages =
        [
            MessageFactory.CreateUserText("修一下构建"),
            AssistantWithCall("call-1", "bash", """{"command":"dotnet build"}"""),
            ToolResult("call-1", "error CS1002"),
            MessageFactory.CreateUserText("继续"),
        ];

        var flattened = SummaryMessagePairing.FlattenToolStructure(messages);

        Assert.DoesNotContain(flattened, message => message.Content.Any(block => block is ToolCallBlock));
        Assert.DoesNotContain(flattened, message => message is ToolResultMessage);
        Assert.DoesNotContain(flattened, message => message.Content.Any(block => block is ToolResultBlock));
    }

    /** 拍平不能丢信息: 工具名、参数、输出、错误标记都要保留成文本。 */
    [Fact]
    public void Flatten_PreservesToolNameArgumentsAndOutput()
    {
        IReadOnlyList<Message> messages =
        [
            AssistantWithCall("call-1", "bash", """{"command":"dotnet test"}"""),
            ToolResult("call-1", "2 tests failed", isError: true),
        ];

        var flattened = SummaryMessagePairing.FlattenToolStructure(messages);

        var text = string.Join("\n", flattened.SelectMany(message => message.Content.OfType<TextBlock>().Select(block => block.Text)));
        Assert.Contains("bash", text, StringComparison.Ordinal);
        Assert.Contains("dotnet test", text, StringComparison.Ordinal);
        Assert.Contains("2 tests failed", text, StringComparison.Ordinal);
        Assert.Contains("call-1", text, StringComparison.Ordinal);
        Assert.Contains(SummaryMessagePairing.ToolErrorMarker, text, StringComparison.Ordinal);
    }

    /** assistant 的正文必须保留: 只替换其中的工具调用块。 */
    [Fact]
    public void Flatten_KeepsAssistantProse()
    {
        IReadOnlyList<Message> messages =
        [
            AssistantWithCall("call-1", "read_file", """{"path":"a.cs"}""", text: "我先看这个文件"),
        ];

        var flattened = SummaryMessagePairing.FlattenToolStructure(messages);

        var assistant = Assert.Single(flattened);
        var text = string.Join("", assistant.Content.OfType<TextBlock>().Select(block => block.Text));
        Assert.Contains("我先看这个文件", text, StringComparison.Ordinal);
        Assert.Contains("read_file", text, StringComparison.Ordinal);
    }

    /** 没有工具结构的普通对话原样返回, 不重建消息对象。 */
    [Fact]
    public void Flatten_LeavesPlainMessagesUntouched()
    {
        var user = MessageFactory.CreateUserText("普通问题");
        IReadOnlyList<Message> messages = [user];

        var flattened = SummaryMessagePairing.FlattenToolStructure(messages);

        Assert.Same(user, Assert.Single(flattened));
    }

    /** 图片不能进摘要请求(服务端不接受), 拍平时降级为占位文本而不是让整次压缩失败。 */
    [Fact]
    public void Flatten_ReplacesImagesWithPlaceholder()
    {
        var image = new ImageBlock(new ImageAttachmentRef("att-1", "image/png", 1024, 8, 8, "shot.png"));
        IReadOnlyList<Message> messages = [MessageFactory.CreateUserMessage([image, new TextBlock("看这张图")])];

        var flattened = SummaryMessagePairing.FlattenToolStructure(messages);

        var message = Assert.Single(flattened);
        Assert.DoesNotContain(message.Content, block => block is ImageBlock);
        var text = string.Join("", message.Content.OfType<TextBlock>().Select(block => block.Text));
        Assert.Contains(SummaryMessagePairing.ImagePlaceholder, text, StringComparison.Ordinal);
        Assert.Contains("看这张图", text, StringComparison.Ordinal);
    }

    /** 孤立的 tool 结果(其调用已不在区间内)也要变成文本, 否则它会让请求整体非法。 */
    [Fact]
    public void Flatten_KeepsOrphanToolResultAsText()
    {
        IReadOnlyList<Message> messages = [ToolResult("call-orphan", "残留输出")];

        var flattened = SummaryMessagePairing.FlattenToolStructure(messages);

        var message = Assert.Single(flattened);
        Assert.NotEqual(MessageRole.Assistant, message.Role);
        Assert.Contains("残留输出", MessageText.Flatten(message.Content), StringComparison.Ordinal);
    }

    /** 有调用无结果(被中断)时必须补一行占位, 否则模型会以为"结果就是空的"。 */
    [Fact]
    public void Flatten_MarksInterruptedCallAsHavingNoOutput()
    {
        IReadOnlyList<Message> messages = [AssistantWithCall("call-cut", "bash", """{"command":"sleep 99"}""")];

        var flattened = SummaryMessagePairing.FlattenToolStructure(messages);

        var text = MessageText.Flatten(Assert.Single(flattened).Content);
        Assert.Contains("call-cut", text, StringComparison.Ordinal);
        Assert.Contains(SummaryMessagePairing.ToolInterruptedMarker, text, StringComparison.Ordinal);
    }

    /** 已有结果的调用不应被误标成中断。 */
    [Fact]
    public void Flatten_DoesNotMarkAnsweredCallAsInterrupted()
    {
        IReadOnlyList<Message> messages =
        [
            AssistantWithCall("call-done", "bash", "{}"),
            ToolResult("call-done", "ok"),
        ];

        var flattened = SummaryMessagePairing.FlattenToolStructure(messages);

        var text = MessageText.Flatten([.. flattened.SelectMany(message => message.Content)]);
        Assert.DoesNotContain(SummaryMessagePairing.ToolInterruptedMarker, text, StringComparison.Ordinal);
    }
}
