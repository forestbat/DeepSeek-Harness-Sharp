using System.Net;
using System.Text;
using System.Text.Json;
using Dsh.Llm;
using Dsh.Llm.Anthropic;

namespace Dsh.Tests;

/** Anthropic Messages 请求体与流: system 角色提到顶层 system 字段, thinking 块带签名回放, message_stop 产出 finish。 */
public class AnthropicMessagesTests
{
    private const string Model = "claude-opus-5";

    private static readonly string[] ToolTurnSse =
    [
        """event: message_start""",
        """data: {"type":"message_start","message":{"id":"msg_1","role":"assistant","content":[],"usage":{"input_tokens":12,"output_tokens":0}}}""",
        "",
        """data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""",
        "",
        """data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig-1"}}""",
        "",
        """data: {"type":"content_block_stop","index":0}""",
        "",
        """data: {"type":"content_block_start","index":1,"content_block":{"type":"redacted_thinking","data":"opaque-1"}}""",
        "",
        """data: {"type":"content_block_stop","index":1}""",
        "",
        """data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_1","name":"echo","input":{}}}""",
        "",
        """data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"text\":\"a\"}"}}""",
        "",
        """data: {"type":"content_block_stop","index":2}""",
        "",
        """data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":7}}""",
        "",
        """data: {"type":"message_stop"}""",
        "",
    ];

    [Fact]
    public async Task System_Messages_Move_To_Top_Level_System_In_Order()
    {
        var handler = new ScriptedHandler(ToolTurnSse);
        var options = Options(
        [
            MessageFactory.CreateSystemMessage([new TextBlock("persona")], new PluginMessageSource("test")),
            MessageFactory.CreateUserText("hi"),
            MessageFactory.CreateSystemMessage([new TextBlock("reminder")], new PluginMessageSource("test")),
        ]);

        await Drain(Adapter(handler), options);

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        var system = body.RootElement.GetProperty("system").EnumerateArray()
            .Select(block => (block.GetProperty("type").GetString(), block.GetProperty("text").GetString()))
            .ToArray();
        Assert.Equal([("text", "persona"), ("text", "reminder")], system);
        var roles = body.RootElement.GetProperty("messages").EnumerateArray()
            .Select(message => message.GetProperty("role").GetString())
            .ToArray();
        Assert.Equal(["user"], roles);
    }

    [Fact]
    public async Task Tool_Turn_Finishes_And_Replays_Thinking_With_Signatures()
    {
        var handler = new ScriptedHandler(ToolTurnSse);
        var adapter = Adapter(handler);
        var user = MessageFactory.CreateUserText("echo a");
        var assembler = new BlockAssembler();
        await foreach (var chunk in adapter.Stream(Options([user]), CancellationToken.None))
            assembler.Push(chunk);

        Assert.IsType<FinishReason.ToolCalls>(assembler.Finish);
        var produced = MessageFactory.CreateAssistantMessage(assembler.Blocks(), "anthropic", Model, assembler.ReplayState?.Response);
        // 恢复会话走的是持久化后的消息: 回放状态必须经得起 JSON 往返。
        var assistant = DshJson.Deserialize<Message>(DshJson.Serialize<Message>(produced))!;
        var result = MessageFactory.CreateToolResultMessage(ToolCallId.Create("toolu_1"), [new TextBlock("a")], false);
        await Drain(adapter, Options([user, assistant, result]));

        using var body = JsonDocument.Parse(handler.Bodies[1]);
        var content = body.RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal(3, content.GetArrayLength());
        Assert.Equal("thinking", content[0].GetProperty("type").GetString());
        Assert.Equal("", content[0].GetProperty("thinking").GetString());
        Assert.Equal("sig-1", content[0].GetProperty("signature").GetString());
        Assert.Equal("redacted_thinking", content[1].GetProperty("type").GetString());
        Assert.Equal("opaque-1", content[1].GetProperty("data").GetString());
        Assert.Equal("tool_use", content[2].GetProperty("type").GetString());
        Assert.Equal("a", content[2].GetProperty("input").GetProperty("text").GetString());
    }

    [Fact]
    public async Task Tool_Result_Image_Maps_To_Native_Image_Block()
    {
        var handler = new ScriptedHandler(ToolTurnSse);
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var image = new ImageAttachmentRef("img-1", "image/png", png.Length, 1, 1, "shot.png");
        var callId = ToolCallId.Create("toolu_1");
        var options = new GenerateOptions
        {
            Provider = "anthropic",
            Model = Model,
            Messages =
            [
                MessageFactory.CreateUserText("look"),
                MessageFactory.CreateAssistantMessage([new ToolCallBlock(callId, "read_image", """{"file_path":"shot.png"}""")], "anthropic", Model),
                MessageFactory.CreateToolResultMessage(callId, [new TextBlock("envelope"), new ImageBlock(image)], false),
            ],
            Attachments = new Dictionary<string, byte[]> { [image.AttachmentId] = png },
        };

        await Drain(Adapter(handler), options);

        using var body = JsonDocument.Parse(handler.Bodies[0]);
        var result = body.RootElement.GetProperty("messages")[2].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        var content = result.GetProperty("content");
        Assert.Equal("envelope", content[0].GetProperty("text").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        var source = content[1].GetProperty("source");
        Assert.Equal(("base64", "image/png", Convert.ToBase64String(png)),
            (source.GetProperty("type").GetString(), source.GetProperty("media_type").GetString(), source.GetProperty("data").GetString()));
    }

    [Fact]
    public void ResolveModel_Reports_ModelsDev_Input_Modalities()
    {
        var adapter = Adapter(new ScriptedHandler(ToolTurnSse));

        Assert.Contains("image", adapter.ResolveModel("claude-haiku-4-5").InputModalities!);
        Assert.Null(adapter.ResolveModel("not-in-models-dev").InputModalities);
    }

    /** 上下文窗口是自动压缩的阈值来源: 目录里有 limit.context 就报出来, 没有则留未知。 */
    [Fact]
    public void ResolveModel_Reports_ModelsDev_ContextWindow()
    {
        var adapter = Adapter(new ScriptedHandler(ToolTurnSse));

        Assert.Equal(200_000, adapter.ResolveModel("claude-haiku-4-5").ContextWindow);
        Assert.Null(adapter.ResolveModel("not-in-models-dev").ContextWindow);
    }

    private static AnthropicAdapter Adapter(HttpMessageHandler handler)
        => new("anthropic", "https://example.invalid", "sk-test", [Model], new HttpClient(handler));

    private static GenerateOptions Options(IReadOnlyList<Message> messages)
        => new()
        {
            Provider = "anthropic",
            Model = Model,
            Messages = messages,
        };

    private static async Task Drain(AnthropicAdapter adapter, GenerateOptions options)
    {
        await foreach (var _ in adapter.Stream(options, CancellationToken.None))
        {
        }
    }

    private sealed class ScriptedHandler(IReadOnlyList<string> sseLines) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{string.Join('\n', sseLines)}\n", Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
