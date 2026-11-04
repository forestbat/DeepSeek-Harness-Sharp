using System.Net;
using System.Text;
using System.Text.Json;
using Dsh.Llm;
using Dsh.Llm.OpenAi;

namespace Dsh.Tests;

/** openai-compatible 适配器的推理强度解析: 端点实时元数据优先, 其次内置 models.dev 快照。 */
public class OpenAiCompatibleAdapterTests
{
    [Fact]
    public void ResolveModel_Uses_ModelsDev_For_Known_Provider()
    {
        var adapter = new OpenAiCompatibleAdapter(
            "deepseek",
            "https://api.deepseek.com",
            "sk-test",
            [new ProviderModelSpec("deepseek-flash")]);

        var info = adapter.ResolveModel("deepseek-flash");

        Assert.Equal(["off", "low", "high", "max"], info.Reasoning!.Efforts.Select(effort => effort.Id.Value));
        Assert.Equal("high", info.Reasoning.DefaultEffort?.ToString());
        Assert.Null(adapter.ResolveModel("unknown-model").Reasoning);
    }

    [Fact]
    public void ResolveModel_Unknown_Provider_Has_No_Reasoning()
    {
        var adapter = new OpenAiCompatibleAdapter(
            "boomfirst",
            "https://example.invalid/v1",
            "sk-test",
            [new ProviderModelSpec("gpt-6-luna")]);

        Assert.Null(adapter.ResolveModel("gpt-6-luna").Reasoning);
    }

    [Fact]
    public void ResolveModel_Uses_Online_Metadata_Before_ModelsDev()
    {
        var online = new LlmModelReasoningInfo(
            [
                new LlmReasoningEffortInfo(ReasoningEffortId.Create("low"), "Low"),
                new LlmReasoningEffortInfo(ReasoningEffortId.Create("max"), "Max"),
            ],
            ReasoningEffortId.Create("max"));
        var adapter = new OpenAiCompatibleAdapter(
            "deepseek",
            "https://api.deepseek.com",
            "sk-test",
            [new ProviderModelSpec("deepseek-flash")],
            metadataSource: new StubReasoningSource("deepseek-flash", online));

        var info = adapter.ResolveModel("deepseek-flash");

        Assert.Equal(["low", "max"], info.Reasoning!.Efforts.Select(effort => effort.Id.Value));
        Assert.Equal("max", info.Reasoning.DefaultEffort?.ToString());
        Assert.Null(adapter.ResolveModel("other-model").Reasoning);
    }

    [Fact]
    public void Parse_Reads_Variants_And_Supported_Parameters()
    {
        const string json = """
            {"data":[
              {"id":"kilo/stealth-a","opencode":{"variants":"@{none=; low=; medium=; high=; xhigh=; max=}"},"supported_parameters":["tools","reasoning"]},
              {"id":"kilo/generic-effort","opencode":{},"supported_parameters":["reasoning","reasoning_effort"]},
              {"id":"kilo/plain"},
              {"id":"kilo/empty-variants","opencode":{"variants":""}},
              {"id":"kilo/object-variants","opencode":{"variants":{"low":"","high":""}}}
            ]}
            """;

        var parsed = ModelMetadataReasoningSource.Parse(json);

        Assert.Equal(["none", "low", "medium", "high", "xhigh", "max"], parsed["kilo/stealth-a"].Efforts.Select(effort => effort.Id.Value));
        Assert.Equal("medium", parsed["kilo/stealth-a"].DefaultEffort?.ToString());
        Assert.Equal(["low", "medium", "high"], parsed["kilo/generic-effort"].Efforts.Select(effort => effort.Id.Value));
        Assert.Equal(["low", "high"], parsed["kilo/object-variants"].Efforts.Select(effort => effort.Id.Value));
        Assert.False(parsed.ContainsKey("kilo/plain"));
        Assert.False(parsed.ContainsKey("kilo/empty-variants"));
    }

    [Fact]
    public void ResolveModel_Reports_ModelsDev_Input_Modalities()
    {
        var known = new OpenAiCompatibleAdapter("deepseek", "https://api.deepseek.com", "sk-test", metadataSource: new NoReasoningSource());
        var relay = new OpenAiCompatibleAdapter("relay", "https://example.invalid/v1", "sk-test", metadataSource: new NoReasoningSource());

        Assert.Equal(["text"], known.ResolveModel("deepseek-v4-pro").InputModalities);
        Assert.Contains("image", known.ResolveModel("deepseek-flash").InputModalities!);
        Assert.Null(relay.ResolveModel("deepseek-flash").InputModalities);
    }

    /** 上下文窗口是自动压缩的阈值来源: 目录里有 limit.context 就报出来, 中转站两路都命中不了则留未知。 */
    [Fact]
    public void ResolveModel_Reports_ModelsDev_ContextWindow()
    {
        var known = new OpenAiCompatibleAdapter("deepseek", "https://api.deepseek.com", "sk-test", metadataSource: new NoReasoningSource());
        var relay = new OpenAiCompatibleAdapter("relay", "https://example.invalid/v1", "sk-test", metadataSource: new NoReasoningSource());

        Assert.Equal(1_000_000, known.ResolveModel("deepseek-v4-flash").ContextWindow);
        Assert.Null(relay.ResolveModel("deepseek-v4-flash").ContextWindow);
    }

    [Fact]
    public async Task ChatCompletions_Relays_Tool_Result_Images_In_A_Following_User_Message()
    {
        var (body, png) = await CaptureToolResultImageRequest(useResponses: false);

        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "tool", "tool", "user"], messages.Select(message => message.GetProperty("role").GetString()));
        Assert.Equal("envelope", messages[2].GetProperty("content").GetString());
        var relayed = messages[4].GetProperty("content").EnumerateArray().ToList();
        Assert.Contains("call_1", relayed[0].GetProperty("text").GetString());
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(png)}", relayed[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task Responses_Relays_Tool_Result_Images_After_The_Function_Output()
    {
        var (body, png) = await CaptureToolResultImageRequest(useResponses: true);

        var input = body.RootElement.GetProperty("input").EnumerateArray().ToList();
        var output = input.FindLastIndex(item => item.GetProperty("type").GetString() == "function_call_output");
        var image = input.FindIndex(item => item.GetRawText().Contains(Convert.ToBase64String(png), StringComparison.Ordinal));
        Assert.True(output >= 0 && image > output, body.RootElement.GetRawText());
        Assert.Contains("input_image", input[image].GetRawText());
    }

    private static async Task<(JsonDocument Body, byte[] Png)> CaptureToolResultImageRequest(bool useResponses)
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var image = new ImageAttachmentRef("img-1", "image/png", png.Length, 1, 1, "shot.png");
        var callId = ToolCallId.Create("call_1");
        var parallelId = ToolCallId.Create("call_2");
        var handler = new CapturingHandler();
        var adapter = new OpenAiCompatibleAdapter(
            "relay", "https://example.invalid/v1", "sk-test",
            httpClient: new HttpClient(handler), useResponses: useResponses,
            metadataSource: new NoReasoningSource());
        var options = new GenerateOptions
        {
            Provider = "relay",
            Model = "vision-model",
            Messages =
            [
                MessageFactory.CreateUserText("look"),
                MessageFactory.CreateAssistantMessage(
                    [new ToolCallBlock(callId, "read_image", """{"file_path":"shot.png"}"""), new ToolCallBlock(parallelId, "bash", """{"command":"ls"}""")],
                    "relay",
                    "vision-model"),
                MessageFactory.CreateToolResultMessage(callId, [new TextBlock("envelope"), new ImageBlock(image)], false),
                MessageFactory.CreateToolResultMessage(parallelId, [new TextBlock("shot.png")], false),
            ],
            Attachments = new Dictionary<string, byte[]> { [image.AttachmentId] = png },
        };
        try
        {
            await foreach (var _ in adapter.Stream(options, CancellationToken.None))
            {
            }
        }
        catch (LlmException)
        {
            // The scripted reply is a chat-completions stream; only the captured request body matters here.
        }
        return (JsonDocument.Parse(handler.Body!), png);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private const string Sse = """
            data: {"id":"c1","object":"chat.completion.chunk","created":0,"model":"vision-model","choices":[{"index":0,"delta":{"role":"assistant","content":"ok"}}]}

            data: {"id":"c1","object":"chat.completion.chunk","created":0,"model":"vision-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{Sse}\n", Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class NoReasoningSource : IModelReasoningSource
    {
        public LlmModelReasoningInfo? ReasoningFor(string model) => null;
    }

    private sealed class StubReasoningSource(string model, LlmModelReasoningInfo reasoning) : IModelReasoningSource
    {
        public LlmModelReasoningInfo? ReasoningFor(string candidate)
            => candidate == model ? reasoning : null;
    }
}
