using System.Net;
using System.Text;
using Dsh.Llm.OpenAi;

namespace Dsh.Tests;

/**
 * 端点自报的上下文窗口: 自建中转站的模型没有 models.dev 条目, 自动压缩只能问端点要。
 * 解析要容忍各家字段名, 并且不能每轮都打网关。
 */
public sealed class EndpointContextWindowTests
{
    private const string Payload = """
        {"data":[
          {"id":"deepseek-v4.1-flash-1017","context_length":131072,"max_tokens":8192},
          {"id":"gateway-window","context_window":65536},
          {"id":"gateway-max-input","max_input_tokens":200000},
          {"id":"openrouter-style","top_provider":{"context_length":32000}},
          {"id":"output-only","max_output_tokens":4096},
          {"id":"zero","context_length":0}
        ]}
        """;

    [Fact]
    public void ParsesCommonContextFieldNames_AndIgnoresOutputLimits()
    {
        var windows = ModelMetadataReasoningSource.ParseContextWindows(Payload);

        Assert.Equal(131072, windows["deepseek-v4.1-flash-1017"]);
        Assert.Equal(65536, windows["gateway-window"]);
        Assert.Equal(200000, windows["gateway-max-input"]);
        Assert.Equal(32000, windows["openrouter-style"]);
        // 输出上限不是上下文窗口; 0 也不算声明。
        Assert.DoesNotContain("output-only", windows.Keys);
        Assert.DoesNotContain("zero", windows.Keys);
    }

    [Fact]
    public void ReturnsNothing_WhenTheEndpointDeclaresNoWindow()
    {
        Assert.Empty(ModelMetadataReasoningSource.ParseContextWindows("""{"data":[{"id":"plain"}]}"""));
        Assert.Empty(ModelMetadataReasoningSource.ParseContextWindows("""{"nope":[]}"""));
    }

    [Fact]
    public async Task ProbesOnceAndCaches_SoATurnDoesNotHitTheGatewayEveryTime()
    {
        var handler = new CountingHandler(Payload);
        using var http = new HttpClient(handler);
        var source = new ModelMetadataReasoningSource("https://gateway.test/v1", "key", http);

        Assert.Equal(131072, await source.EnsureContextWindowAsync("deepseek-v4.1-flash-1017", TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Requests);
        // 缓存命中: 同一个模型再问不再发请求。
        Assert.Equal(131072, await source.EnsureContextWindowAsync("deepseek-v4.1-flash-1017", TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task ReturnsNull_WhenTheEndpointFails_AndStaysQuietForAWhile()
    {
        var handler = new CountingHandler(Payload, HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(handler);
        var source = new ModelMetadataReasoningSource("https://gateway.test/v1", "key", http);

        Assert.Null(await source.EnsureContextWindowAsync("deepseek-v4.1-flash-1017", TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Requests);
        // 网关坏着的时候不能每轮都白等一次探测超时。
        Assert.Null(await source.EnsureContextWindowAsync("deepseek-v4.1-flash-1017", TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Requests);
    }

    private sealed class CountingHandler(string payload, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests += 1;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }
}
