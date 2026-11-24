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

    /**
     * 中转站的模型不在 models.dev 里, 窗口只能靠用户手填:
     * settings 的 providers.<id>.models.<model>.context_window / max_output_tokens 必须直接生效。
     */
    [Fact]
    public void ResolveModel_Prefers_TheDeclaredContextWindowOverTheCatalog()
    {
        var adapter = new OpenAiCompatibleAdapter(
            "relay",
            "https://example.invalid/v1",
            "sk-test",
            [new ProviderModelSpec("some-model", ContextWindow: 1_000_000, MaxOutputTokens: 384_000)]);

        var info = adapter.ResolveModel("some-model");

        Assert.Equal(1_000_000, info.ContextWindow);
        Assert.Equal(384_000, info.DefaultMaxTokens);
    }

    /** 廉价探测: 服务端在生成前拒绝 max_tokens, 报错里写了自己的上限 —— 从报错里读出来即可。 */
    [Theory]
    [InlineData("""{"error":{"message":"max_tokens must be <= 8192"}}""", 8192, false)]
    [InlineData("""{"error":{"message":"This model's maximum context length is 65536 tokens"}}""", 65536, true)]
    [InlineData("""{"error":{"message":"Requested token count exceeds the configured maximum output length of 393216 tokens."}}""", 393216, false)]
    [InlineData("""{"error":{"message":"max_completion_tokens is too large: 1000000000. This model supports at most 65536"}}""", 65536, false)]
    public void ParseDeclaredLimit_ReadsTheLimitFromTheErrorText(string body, int expected, bool isContextWindow)
    {
        var limit = ModelMetadataReasoningSource.ParseDeclaredLimit(body);

        Assert.True(limit is not null);
        Assert.Equal(expected, limit.Value.Value);
        Assert.Equal(isContextWindow, limit.Value.IsContextWindow);
    }

    [Fact]
    public void ParseDeclaredLimit_IgnoresMessagesWithoutALimit()
        => Assert.Null(ModelMetadataReasoningSource.ParseDeclaredLimit("""{"error":{"message":"invalid model"}}"""));

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

    /** 推理强度的出站值按字面被端点校验: "max" 必须发成 xhigh, 发成 "max"/"extrahigh" 等于该档位不可用。 */
    [Fact]
    public async Task Reasoning_Effort_Max_Is_Sent_As_Xhigh()
    {
        var chat = await CaptureReasoningBodyAsync("max", useResponses: false);
        var responses = await CaptureReasoningBodyAsync("max", useResponses: true);

        Assert.Contains("\"reasoning_effort\":\"xhigh\"", chat);
        Assert.Contains("\"effort\":\"xhigh\"", responses);
    }

    private static async Task<string> CaptureReasoningBodyAsync(string effort, bool useResponses)
    {
        var handler = new CapturingHandler();
        var adapter = new OpenAiCompatibleAdapter(
            "relay",
            "https://example.invalid/v1",
            "sk-test",
            httpClient: new HttpClient(handler),
            useResponses: useResponses,
            metadataSource: new NoReasoningSource());
        var options = new GenerateOptions
        {
            Provider = "relay",
            Model = "vision-model",
            Messages = [MessageFactory.CreateUserText("hi")],
            ReasoningEffort = ReasoningEffortId.Create(effort),
        };
        try
        {
            await foreach (var _ in adapter.Stream(options, CancellationToken.None))
            {
            }
        }
        catch (LlmException)
        {
            // 脚本化的响应只有 chat 流; 这里只关心出站请求体。
        }
        return handler.Body ?? "";
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

    /**
     * Responses 通道在输出被截断时以 response.incomplete 收尾, 全程不带 finish_reason。
     * 截断是正常收尾(长度用尽), 不能当成流中断: 否则整轮回复被 STREAM_CLOSED 丢掉,
     * 推理强度探针也因此永远记不下结论(deepseek-ly 那类推理模型 + 小输出预算必然踩到)。
     */
    [Fact]
    public async Task Responses_Incomplete_Stream_Finishes_As_MaxTokens()
    {
        var adapter = new OpenAiCompatibleAdapter(
            "relay",
            "https://example.invalid/v1",
            "sk-test",
            httpClient: new HttpClient(new TruncatedResponsesHandler()),
            useResponses: true,
            metadataSource: new NoReasoningSource());
        var options = new GenerateOptions
        {
            Provider = "relay",
            Model = "relay-model",
            Messages = [MessageFactory.CreateUserText("ping")],
            ReasoningEffort = ReasoningEffortId.Create("low"),
            MaxTokens = 16,
        };

        var finishes = new List<FinishReason>();
        await foreach (var chunk in adapter.Stream(options, CancellationToken.None))
        {
            if (chunk is StreamChunk.Finish finish)
                finishes.Add(finish.Reason);
        }

        var reason = Assert.Single(finishes);
        Assert.IsType<FinishReason.MaxTokens>(reason);
    }

    /** 端点收下请求后把输出截断在 16 个 token: Responses 流以 response.incomplete 收尾, 没有 finish_reason。 */
    private sealed class TruncatedResponsesHandler : HttpMessageHandler
    {
        private const string Sse = """
            event: response.created
            data: {"type":"response.created","response":{"id":"resp_1","object":"response","created_at":1795356484,"model":"relay-model","status":"in_progress","output":[]},"sequence_number":0}

            event: response.incomplete
            data: {"type":"response.incomplete","response":{"id":"resp_1","object":"response","created_at":1795356484,"model":"relay-model","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[],"usage":{"input_tokens":1,"output_tokens":16,"total_tokens":17}},"sequence_number":1}

            """;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{Sse}\n", Encoding.UTF8, "text/event-stream"),
            });
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
