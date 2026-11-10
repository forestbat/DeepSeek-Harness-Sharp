using System.Net;
using System.Text;
using Dsh.Llm;
using Dsh.Llm.Anthropic;

namespace Dsh.Tests;

/**
 * anthropic-messages 的中转站同样不在 /v1/models 里公布推理档位(capabilities.effort 只有官方才有),
 * 所以 adapter 用一次故意非法的 output_config.effort 去问端点: 网关一般会在报错里列出合法取值。
 */
public sealed class AnthropicReasoningEffortProbeTests
{
    private const string Endpoint = "https://example.invalid";

    /** 实测网关(net-api)的报错原文: 取值清单就写在 message 里。 */
    private const string RejectionBody = """
        {"error":{"type":"<nil>","message":"output_config.effort must be one of minimal, low, medium, high, xhigh, max, or ultra. (request id: 202611081704398793346488268d9d6np5s5Smm)"},"type":"error"}
        """;

    private const string MessageSse = """
        event: message_start
        data: {"type":"message_start","message":{"id":"m1","type":"message","role":"assistant","model":"relay-model","content":[],"stop_reason":null,"usage":{"input_tokens":1,"output_tokens":0}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"pong"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    /** 网关在报错里列了取值 → 用端点声明的档位, 而且只问一次。 */
    [Fact]
    public async Task Probe_Adopts_Declared_Efforts_From_The_Rejection()
    {
        var endpoint = new StubEndpoint(_ => Text(HttpStatusCode.BadRequest, RejectionBody));
        var adapter = CreateAdapter(endpoint);

        Assert.Null(adapter.ResolveModel("relay-model").Reasoning);
        await WaitForReasoningAsync(adapter);

        var efforts = adapter.ResolveModel("relay-model").Reasoning!;
        Assert.Equal(
            ["minimal", "low", "medium", "high", "xhigh", "max", "ultra"],
            efforts.Efforts.Select(effort => effort.Id.Value));
        Assert.Equal("medium", efforts.DefaultEffort?.ToString());
        Assert.Equal(1, endpoint.Messages);
        Assert.Contains("\"effort\":\"__dsh_probe__\"", endpoint.LastBody);
    }

    /** 端点不校验(非法值也 200)→ 按 Anthropic 族通用档位给; 校验但列不出取值 → 不显示档位。 */
    [Fact]
    public async Task Probe_Falls_Back_To_Standard_Efforts_When_The_Endpoint_Does_Not_Validate()
    {
        var endpoint = new StubEndpoint(_ => Text(HttpStatusCode.OK, MessageSse));
        var adapter = CreateAdapter(endpoint);

        Assert.Null(adapter.ResolveModel("relay-model").Reasoning);
        await WaitForReasoningAsync(adapter);

        Assert.Equal(
            ["low", "medium", "high", "max", "xhigh"],
            adapter.ResolveModel("relay-model").Reasoning!.Efforts.Select(effort => effort.Id.Value));
        Assert.Equal(1, endpoint.Messages);
    }

    /** 结论写进缓存文件: 换一个适配器/重启后直接用, 不再打端点。 */
    [Fact]
    public async Task Probe_Result_Survives_A_New_Adapter()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-anthropic-efforts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var cacheFile = Path.Combine(root, "reasoning-efforts.json");
            var first = new StubEndpoint(_ => Text(HttpStatusCode.BadRequest, RejectionBody));
            var probed = CreateAdapter(first, new ReasoningEffortsCache(cacheFile));
            Assert.Null(probed.ResolveModel("relay-model").Reasoning);
            await WaitForReasoningAsync(probed);
            Assert.Equal(1, first.Messages);

            var second = new StubEndpoint(_ => Text(HttpStatusCode.InternalServerError, "{}"));
            var restarted = CreateAdapter(second, new ReasoningEffortsCache(cacheFile));

            Assert.Equal(
                ["minimal", "low", "medium", "high", "xhigh", "max", "ultra"],
                restarted.ResolveModel("relay-model").Reasoning!.Efforts.Select(effort => effort.Id.Value));
            Assert.Equal(0, second.Messages);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /** 报错文本的取值清单解析: 只认清单本体, request id 之类的尾巴不能混进来。 */
    [Fact]
    public void ParseSupported_Reads_The_Declaration_And_Ignores_Prose()
    {
        Assert.Equal(
            ["minimal", "low", "medium", "high", "xhigh", "max", "ultra"],
            AnthropicEffortValues.ParseSupported(
                """output_config.effort must be one of minimal, low, medium, high, xhigh, max, or ultra. (request id: abc)"""));
        Assert.Equal(
            ["low", "high"],
            AnthropicEffortValues.ParseSupported("Supported values are 'low' and 'high'."));
        Assert.Empty(AnthropicEffortValues.ParseSupported("invalid output_config.effort"));
        Assert.Empty(AnthropicEffortValues.ParseSupported(null));
    }

    private static AnthropicAdapter CreateAdapter(StubEndpoint endpoint, ReasoningEffortsCache? cache = null)
        => new(
            "relay",
            Endpoint,
            "sk-test",
            httpClient: new HttpClient(endpoint),
            reasoningEfforts: cache);

    private static async Task WaitForReasoningAsync(LlmAdapter adapter)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (adapter.ResolveModel("relay-model")?.Reasoning is null && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    private static HttpResponseMessage Text(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubEndpoint(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _messages;

        /** 只数 /v1/messages: 适配器启动时还会 GET /v1/models 取模型清单。 */
        public int Messages => Volatile.Read(ref _messages);

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref _messages);
                LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return respond(request);
        }
    }
}
