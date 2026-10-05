using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Llm;
using Dsh.Llm.DeepSeek;

namespace Dsh.Tests;

/** DeepSeek wire 序列化契约: tool_calls/tools 必须是 {type:"function", function:{...}} 形状。 */
public sealed class DeepSeekWireSerializeTests
{
    private static JsonObject SerializeAssistant(Message message)
    {
        var options = new GenerateOptions
        {
            Provider = "deepseek-official",
            Model = "deepseek-v4-flash",
            Messages = [message],
        };
        var wire = WireSerialize.SerializeMessages(options);
        var json = JsonSerializer.Serialize(wire[0], DeepSeekWireJsonContext.Default.WireMessage);
        return JsonNode.Parse(json)!.AsObject();
    }

    [Fact]
    public void ToolCall_EmitsOpenAiWireShape()
    {
        var wire = SerializeAssistant(new Message
        {
            Id = MessageFactory.NewId(),
            Role = MessageRole.Assistant,
            Content = [new ToolCallBlock(ToolCallId.Create("call_00_x"), "bash", """{"command":"ls"}""")],
        });

        var call = wire["tool_calls"]![0]!;
        Assert.Equal("function", call["type"]!.GetValue<string>());
        Assert.Equal("call_00_x", call["id"]!.GetValue<string>());
        Assert.Equal("bash", call["function"]!["name"]!.GetValue<string>());
        Assert.Equal("""{"command":"ls"}""", call["function"]!["arguments"]!.GetValue<string>());
        Assert.False(wire.ContainsKey("reasoning_content"));
    }

    [Fact]
    public void ToolCallWithReasoning_EmitsReasoningText()
    {
        var wire = SerializeAssistant(new Message
        {
            Id = MessageFactory.NewId(),
            Role = MessageRole.Assistant,
            Content =
            [
                new ReasoningBlock("用户要列目录"),
                new ToolCallBlock(ToolCallId.Create("toon-0"), "bash", """{"command":"ls"}"""),
            ],
        });

        Assert.Equal("用户要列目录", wire["reasoning_content"]!.GetValue<string>());
    }

    [Fact]
    public void PlainText_OmitsReasoningContent()
    {
        var wire = SerializeAssistant(new Message
        {
            Id = MessageFactory.NewId(),
            Role = MessageRole.Assistant,
            Content = [new TextBlock("你好")],
        });

        Assert.False(wire.ContainsKey("reasoning_content"));
    }
}
