using System.Net;
using System.Text;
using Dsh.Llm;
using Dsh.Llm.OpenAi;

namespace Dsh.Tests;

/**
 * 中转站/网关的 /models 不发布推理强度(没有 supported_parameters, 也没有 anthropic 的 capabilities.effort),
 * 所以只能拿一次最小请求去问端点认不认 reasoning_effort: 认就给出通用档位, 明确拒绝就不再显示。
 * 结论落盘, 重启/new 适配器都直接复用。
 */
public sealed class ReasoningEffortProbeTests
{
    /** 适配器把 baseUrl 尾巴上的斜杠去掉后再当缓存键, 断言也用同一个形态。 */
    private const string Endpoint = "https://example.invalid/v1";

    private const string ChatSse = """
        data: {"id":"c1","object":"chat.completion.chunk","created":0,"model":"relay-model","choices":[{"index":0,"delta":{"role":"assistant","content":"pong"}}]}

        data: {"id":"c1","object":"chat.completion.chunk","created":0,"model":"relay-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

        data: [DONE]

        """;

    /** 端点接受 reasoning_effort → 档位可用, 而且只问一次。 */
    [Fact]
    public async Task Probe_Adopts_Standard_Efforts_After_One_Accepted_Request()
    {
        var endpoint = new StubEndpoint(_ => Text(HttpStatusCode.OK, ChatSse, "text/event-stream"));
        var adapter = CreateAdapter(endpoint);

        Assert.Null(adapter.ResolveModel("relay-model").Reasoning);
        await WaitForReasoningAsync(adapter);

        var efforts = adapter.ResolveModel("relay-model").Reasoning!;
        Assert.Equal(["low", "medium", "high", "max"], efforts.Efforts.Select(effort => effort.Id.Value));
        Assert.Equal("medium", efforts.DefaultEffort?.ToString());
        Assert.Equal(1, endpoint.Posts);
        Assert.Contains("\"reasoning_effort\":\"low\"", endpoint.LastBody);
    }

    /** 端点明确拒绝 reasoning_effort → 不再显示档位, 也不会反复打扰端点。 */
    [Fact]
    public async Task Probe_Drops_Efforts_When_Endpoint_Rejects_Them()
    {
        var endpoint = new StubEndpoint(_ => Text(
            HttpStatusCode.BadRequest,
            """{"error":{"message":"unsupported reasoning effort: \"low\"","type":"new_api_error"}}""",
            "application/json"));
        var cache = new ReasoningEffortsCache(null);
        var adapter = CreateAdapter(endpoint, cache);

        Assert.Null(adapter.ResolveModel("relay-model").Reasoning);
        await WaitUntilAsync(() => cache.TryGet(ReasoningEffortsCache.Key("openai-compatible", Endpoint, "relay-model"), out _));

        Assert.Null(adapter.ResolveModel("relay-model").Reasoning);
        Assert.Equal(1, endpoint.Posts);
    }

    /** 结论写进缓存文件: 换一个适配器/重启后直接用, 不再打端点。 */
    [Fact]
    public async Task Probe_Result_Survives_A_New_Adapter()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-reasoning-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var cacheFile = Path.Combine(root, "reasoning-efforts.json");
            var first = new StubEndpoint(_ => Text(HttpStatusCode.OK, ChatSse, "text/event-stream"));
            var probed = CreateAdapter(first, new ReasoningEffortsCache(cacheFile));
            Assert.Null(probed.ResolveModel("relay-model").Reasoning);
            await WaitForReasoningAsync(probed);
            Assert.Equal(1, first.Posts);

            var second = new StubEndpoint(_ => Text(HttpStatusCode.InternalServerError, "{}", "application/json"));
            var restarted = CreateAdapter(second, new ReasoningEffortsCache(cacheFile));

            Assert.Equal(
                ["low", "medium", "high", "max"],
                restarted.ResolveModel("relay-model").Reasoning!.Efforts.Select(effort => effort.Id.Value));
            Assert.Equal(0, second.Posts);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /** 目录(或端点元数据)已经知道这个模型时不探测: 目录说"不支持推理"就是结论, 不该为它花钱发请求。 */
    [Fact]
    public void Catalogued_Model_Without_Reasoning_Is_Not_Probed()
    {
        var endpoint = new StubEndpoint(_ => Text(HttpStatusCode.OK, ChatSse, "text/event-stream"));
        var adapter = new OpenAiCompatibleAdapter(
            "relay",
            "https://example.invalid/v1",
            "sk-test",
            httpClient: new HttpClient(endpoint));

        Assert.Null(adapter.ResolveModel("deepseek-v4-flash").Reasoning);
        Assert.Equal(0, endpoint.Posts);
    }

    private static OpenAiCompatibleAdapter CreateAdapter(StubEndpoint endpoint, ReasoningEffortsCache? cache = null)
        => new(
            "relay",
            Endpoint,
            "sk-test",
            httpClient: new HttpClient(endpoint),
            reasoningEfforts: cache);

    private static async Task WaitForReasoningAsync(LlmAdapter adapter)
        => await WaitUntilAsync(() => adapter.ResolveModel("relay-model")?.Reasoning is not null);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    private static HttpResponseMessage Text(HttpStatusCode status, string body, string contentType)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    private sealed class StubEndpoint(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _posts;

        /** 只数对话请求: 适配器启动时还会 GET /models 取模型清单, 那不是探测。 */
        public int Posts => Volatile.Read(ref _posts);

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
                Interlocked.Increment(ref _posts);
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }
}
