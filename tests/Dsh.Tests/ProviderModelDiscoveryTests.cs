using System.Net;
using System.Text;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Llm.OpenAi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dsh.Tests;

/**
 * 中转站/私有端点的模型清单只能从端点自己的 GET /models 拿到。
 * 适配器注册后在后台拉取, 模型选单(声明 ∪ models.dev 目录 ∪ 端点自报)随之列出它们: 用户不用自己抄一遍模型 id。
 */
public sealed class ProviderModelDiscoveryTests
{
    private const string RelayModels = """{"data":[{"id":"relay-a","object":"model"},{"id":"relay-b","object":"model"}]}""";

    /** 真实链路: settings.yaml 里只写 baseUrl/apiKey → 注册适配器 → 后台拉 /models → 选单(ProviderCatalog.EffectiveModels)里出现它们。 */
    [Fact]
    public async Task Compose_Lists_Models_Discovered_From_The_Endpoint()
    {
        await using var endpoint = await ModelEndpoint.StartAsync(RelayModels);
        var root = Path.Combine(Path.GetTempPath(), $"dsh-provider-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "settings.yaml"), $"""
                global_default_model: relay/relay-a
                providers:
                  relay:
                    type: openai-compatible
                    options:
                      baseUrl: {endpoint.BaseUrl}/v1
                      apiKey: sk-test
                """);
            using var app = await HarnessComposer.Compose(
                new HarnessOptions(new HarnessHome(root), root),
                global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
            var llm = app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;

            await WaitForAsync(() => llm.ListModels("relay").Count, expected: 2);

            var settings = HarnessSettings.Load(new HarnessHome(root));
            // 端点自报的模型不落盘(models.dev 与端点都是唯一真相, settings 只留覆盖), 但选单必须列出来。
            Assert.Empty(settings.Providers["relay"].Models);
            Assert.Equal(
                ["relay-a", "relay-b"],
                ProviderCatalog.EffectiveModels(llm, "relay", settings.Providers["relay"]));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /** 声明/目录里的模型在前(带显示名), 端点自报的补在后; 两边都有的 id 只出现一次且保留声明里的显示名。 */
    [Fact]
    public async Task ListModels_Puts_Declared_Models_Before_Discovered()
    {
        var adapter = new OpenAiCompatibleAdapter(
            "relay",
            "https://example.invalid/v1",
            "sk-test",
            [new ProviderModelSpec("declared", "Declared Model")],
            httpClient: new HttpClient(new JsonEndpoint("""{"data":[{"id":"declared"},{"id":"relay-b"}]}""")));

        await WaitForAsync(() => adapter.ListModels().Count, expected: 2);

        Assert.Equal(
            [("declared", "Declared Model"), ("relay-b", "relay-b")],
            adapter.ListModels().Select(model => (model.Id, model.Name)));
    }

    /** 端点响应结构不符或条目残缺时只跳过坏条目, 不抛错(离线/网关返回错误页时不至于连注册都失败)。 */
    [Fact]
    public void ParseIds_Skips_Junk_Entries_And_Duplicates()
    {
        const string json = """
            {"object":"list","data":[
              {"id":"relay-a"},
              {"id":"relay-a"},
              {"id":""},
              {"id":42},
              {"object":"model"},
              "not-an-object",
              {"id":"relay/b"}
            ]}
            """;

        Assert.Equal(["relay-a", "relay/b"], EndpointModelList.ParseIds(json));
        Assert.Empty(EndpointModelList.ParseIds("""{"error":"nope"}"""));
        Assert.Empty(EndpointModelList.ParseIds("""{"data":{}}"""));
    }

    /** 冷却窗口内的重复请求合并成一次, 拉取进行中不重入, 结束后闸门必须重新可用(否则抖动一次就永久静默)。 */
    [Fact]
    public void BackgroundRefreshGate_Merges_Requests_Inside_The_Cooldown()
    {
        var gate = new BackgroundRefreshGate(TimeSpan.FromMinutes(5));

        Assert.True(gate.TryBegin());
        Assert.False(gate.TryBegin());
        gate.End();
        Assert.False(gate.TryBegin());
        Assert.True(new BackgroundRefreshGate(TimeSpan.Zero).TryBegin());
    }

    /** 后台拉取不阻塞注册: 等它落地后再断言, 不等就是拿"注册瞬间"的结果下结论。 */
    private static async Task WaitForAsync(Func<int> count, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (count() < expected && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    /** 固定返回一段 /models 响应的传输层: 适配器自己建元数据源, 这里只替换网络。 */
    private sealed class JsonEndpoint(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    /** 端点只认 GET /models: 返回固定的模型清单, 别的一律 404。 */
    private sealed class ModelEndpoint : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly string _modelsJson;

        private ModelEndpoint(string modelsJson)
        {
            _modelsJson = modelsJson;
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            _application = builder.Build();
            _application.Run(Serve);
        }

        public static async Task<ModelEndpoint> StartAsync(string modelsJson)
        {
            var endpoint = new ModelEndpoint(modelsJson);
            try
            {
                await endpoint._application.StartAsync(TestContext.Current.CancellationToken);
                endpoint.Port = new Uri(endpoint._application.Urls.Single()).Port;
                return endpoint;
            }
            catch
            {
                await endpoint.DisposeAsync();
                throw;
            }
        }

        public int Port { get; private set; }

        public string BaseUrl => $"http://127.0.0.1:{Port}";

        private async Task Serve(HttpContext context)
        {
            if (!HttpMethods.IsGet(context.Request.Method)
                || context.Request.Path.Value?.EndsWith("/models", StringComparison.Ordinal) != true)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(_modelsJson), context.RequestAborted);
        }

        public ValueTask DisposeAsync() => _application.DisposeAsync();
    }
}
