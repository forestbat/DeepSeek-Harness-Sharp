using System.Net;
using System.Text;
using Dsh.Account;
using Dsh.Agent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dsh.Tests;

/**
 * 账号令牌走整条真实链路: 只登录账号、没有 API Key 的 provider 仍被注册,
 * 模型清单来自端点 /models, 对话带 x-dsh-auth-token(而不是 Bearer)。
 */
public sealed class DeepSeekAccountRouteTests
{
    private const string ListingJson = """
        {"object":"list","data":[
          {"id":"deepseek-flash","object":"model","name":"DeepSeek-V4.1-Flash","context_window":1048576,
           "max_output_tokens":393216,"input_modalities":["text","image"],
           "effort":{"supported_levels":["low","high","max"],"default_level":"high"}},
          {"id":"deepseek-v4-pro","object":"model","name":"DeepSeek-V4-Pro","context_window":1048576,
           "max_output_tokens":393216,"input_modalities":["text"],
           "effort":{"supported_levels":["low","high","max"],"default_level":"high"}}]}
        """;

    /** 每行都以 '\n' 结束(含最后一行): SSE 解析器按行推进, 缺行尾会把最后一帧丢掉。 */
    private static readonly string Sse = string.Join('\n',
    [
        """data: {"choices":[{"delta":{"content":"ok"}}]}""",
        "",
        """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""",
        "",
        "data: [DONE]",
        "",
        "",
    ]);

    /** 已登录且没有 API Key: provider 被注册, 模型来自端点, 对话走账号头。 */
    [Fact]
    public async Task SignedInAccount_ServesModelsAndChatWithoutAnApiKey()
    {
        await using var endpoint = await AccountEndpoint.StartAsync();
        using var home = new TestHome(Settings(endpoint, apiKey: null), signedIn: true);
        using var app = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root), global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
        var llm = app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;

        await WaitForAsync(() => ListModels(llm), expected: 2);
        var info = llm.ResolveModelInfo("ds", "deepseek-flash");
        Assert.Equal(1_048_576, info.ContextWindow);
        Assert.Equal(393_216, info.DefaultMaxTokens);
        Assert.Equal(["text", "image"], info.InputModalities);

        var (text, failure) = await StreamAsync(llm, "deepseek-flash");
        Assert.Null(failure);
        Assert.Equal("ok", text);
        var chat = Assert.Single(endpoint.Requests, request => request.Path.EndsWith("/chat/completions", StringComparison.Ordinal));
        Assert.Equal("account-token", chat.Header(LlmCredential.DshAuthTokenHeader));
        Assert.Null(chat.Header("Authorization"));
        var listing = Assert.Single(endpoint.Requests, request => request.Path.EndsWith("/models", StringComparison.Ordinal));
        Assert.Equal("account-token", listing.Header(LlmCredential.DshAuthTokenHeader));
    }

    /** 端点拒绝账号令牌: 失败的分类是"需要重新登录", 并清掉本地登录态, 而不是 API Key 无效。 */
    [Fact]
    public async Task RejectedAccountToken_ReportsAccountTokenInvalid()
    {
        await using var endpoint = await AccountEndpoint.StartAsync(chatStatus: HttpStatusCode.Unauthorized);
        using var home = new TestHome(Settings(endpoint, apiKey: null), signedIn: true);
        using var app = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root), global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
        var llm = app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;

        var (_, failure) = await StreamAsync(llm, "deepseek-flash");

        Assert.Equal(LlmFailureCodes.AccountTokenInvalid, failure!.Code);
        Assert.Equal(401, failure.Status);
        Assert.Null(new AccountStore(home.Home).ReadGrant(endpoint.BaseUrl));
    }

    /** 未登录时 provider 仍注册(注册只看 origin), 但请求以"需要登录"失败, 不会退化成 API Key 错误。 */
    [Fact]
    public async Task SignedOutAccount_FailsWithSignInRequired()
    {
        await using var endpoint = await AccountEndpoint.StartAsync();
        using var home = new TestHome(Settings(endpoint, apiKey: null), signedIn: false);
        using var app = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root), global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
        var llm = app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;

        var (_, failure) = await StreamAsync(llm, "deepseek-flash");

        Assert.Equal(LlmFailureCodes.AccountSignInRequired, failure!.Code);
    }

    /** 有 API Key 时优先 Bearer, 不去碰账号登录态。 */
    [Fact]
    public async Task ApiKey_WinsOverTheAccountTokenAndUsesBearer()
    {
        await using var endpoint = await AccountEndpoint.StartAsync();
        using var home = new TestHome(Settings(endpoint, apiKey: "sk-test"), signedIn: true);
        using var app = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root), global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
        var llm = app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;

        var (text, failure) = await StreamAsync(llm, "deepseek-flash");

        Assert.Null(failure);
        Assert.Equal("ok", text);
        var chat = Assert.Single(endpoint.Requests, request => request.Path.EndsWith("/chat/completions", StringComparison.Ordinal));
        Assert.Equal("Bearer sk-test", chat.Header("Authorization"));
        Assert.Null(chat.Header(LlmCredential.DshAuthTokenHeader));
    }

    private static string Settings(AccountEndpoint endpoint, string? apiKey)
        => $"""
            plugins:
              "@deepseek-ai/dsh-account":
                platformOrigin: {endpoint.BaseUrl}
                inferenceOrigin: {endpoint.BaseUrl}
                allowLoopbackHttp: true
            providers:
              ds:
                type: deepseek
                options:
                  baseUrl: {endpoint.BaseUrl}{(apiKey is null ? "" : $"\n      apiKey: {apiKey}")}
            """;

    private static int ListModels(LlmRuntime llm) => llm.ListProviders().Count == 0 ? 0 : llm.ListModels("ds").Count;

    /** 零配置: settings 里一条 provider 都没有, 只登录了账号, 请求也要落到账号路由上。 */
    [Fact]
    public async Task ZeroConfig_SendsTheFirstListedModelThroughTheAccountRoute()
    {
        await using var endpoint = await AccountEndpoint.StartAsync();
        using var home = new TestHome($"""
            plugins:
              "@deepseek-ai/dsh-account":
                platformOrigin: {endpoint.BaseUrl}
                inferenceOrigin: {endpoint.BaseUrl}
                allowLoopbackHttp: true
            """, signedIn: true);
        using var app = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root), global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
        var ctx = app.Ctx;
        await using var fixture = await AgentFixture.StartAsync(ctx);

        var model = await fixture.RunTurnAsync(TestContext.Current.CancellationToken);

        Assert.Equal("deepseek-flash", model);
        var chats = endpoint.Requests.Where(request => request.Path.EndsWith("/chat/completions", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(chats);
        Assert.All(chats, chat => Assert.Equal("account-token", chat.Header(LlmCredential.DshAuthTokenHeader)));
    }

    /** 用户配了自己的 provider: 兜底不介入, 也不往 settings.yaml 里写 deepseek-account。 */
    [Fact]
    public async Task ConfiguredProvider_KeepsTheFallbackOut()
    {
        await using var endpoint = await AccountEndpoint.StartAsync();
        using var home = new TestHome(Settings(endpoint, apiKey: "sk-test"), signedIn: true);
        using var app = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root), global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
        await using var fixture = await AgentFixture.StartAsync(app.Ctx);

        var model = await fixture.RunTurnAsync(new AgentOptions("ds", "deepseek-flash"), TestContext.Current.CancellationToken);

        var llm = app.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
        Assert.DoesNotContain(llm.ListProviders(), provider => provider.Id == "deepseek-account");
        Assert.Equal("deepseek-flash", model);
        var chats = endpoint.Requests.Where(request => request.Path.EndsWith("/chat/completions", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(chats);
        Assert.All(chats, chat => Assert.Equal("Bearer sk-test", chat.Header("Authorization")));
        var settings = HarnessSettings.Load(home.Home);
        Assert.DoesNotContain("deepseek-account", settings.Providers.Keys);
    }

    /** 兜底路由写回 settings.yaml: 下次启动直接按配置注册, 不必再为"用哪个模型"问一次端点。 */
    [Fact]
    public async Task ZeroConfig_PersistsTheRouteForTheNextStart()
    {
        await using var endpoint = await AccountEndpoint.StartAsync();
        using var home = new TestHome($"""
            plugins:
              "@deepseek-ai/dsh-account":
                platformOrigin: {endpoint.BaseUrl}
                inferenceOrigin: {endpoint.BaseUrl}
                allowLoopbackHttp: true
            """, signedIn: true);
        using (var app = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root), global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime))
        {
            await using var fixture = await AgentFixture.StartAsync(app.Ctx);
            Assert.Equal("deepseek-flash", await fixture.RunTurnAsync(TestContext.Current.CancellationToken));
        }

        var settings = HarnessSettings.Load(home.Home);
        var persisted = Assert.Single(settings.Providers, entry => entry.Key == "deepseek-account").Value;
        Assert.Equal(ProviderTypes.DeepSeek, persisted.Type);
        Assert.Equal(endpoint.BaseUrl, persisted.Options!.BaseUrl);
        Assert.Equal(["deepseek-flash", "deepseek-v4-pro"], persisted.Models.Keys.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal("deepseek-account/deepseek-flash", settings.GlobalDefaultModel);
        Assert.Null(persisted.Options.ApiKey);

        // 第二次启动: 端点清单不再被问, 配置直接生效。
        endpoint.Requests.Clear();
        using var restarted = await HarnessComposer.Compose(new HarnessOptions(home.Home, home.Root), global::Dsh.Plugins.Generated.DshPluginCatalog.CreatePluginRuntime);
        var llm = restarted.Ctx.Get<LlmRuntime>(LlmRuntime.ServiceName)!;
        Assert.Contains(llm.ListProviders(), provider => provider.Id == "deepseek-account");
        Assert.Equal(["deepseek-flash", "deepseek-v4-pro"], llm.ListModels("deepseek-account").Select(model => model.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.DoesNotContain(endpoint.Requests, request => request.Path.EndsWith("/models", StringComparison.Ordinal));
    }

    private static async Task<(string Text, LlmFailure? Failure)> StreamAsync(LlmRuntime llm, string model)
    {
        var options = new GenerateOptions
        {
            Provider = "ds",
            Model = model,
            Messages = [MessageFactory.CreateUserText("hi", new PluginMessageSource("test"))],
        };
        var text = new StringBuilder();
        LlmFailure? failure = null;
        await foreach (var chunk in llm.Stream(options).WithCancellation(TestContext.Current.CancellationToken))
        {
            switch (chunk)
            {
                case StreamChunk.TextDelta delta:
                    text.Append(delta.Text);
                    break;
                case StreamChunk.Finish finish:
                    failure = finish.Reason switch
                    {
                        FinishReason.Error error => error.Failure,
                        FinishReason.Aborted aborted => aborted.Failure,
                        _ => null,
                    };
                    break;
            }
        }
        return (text.ToString(), failure);
    }

    private static async Task WaitForAsync(Func<int> count, int expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (count() < expected && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    /** 独立 home; 需要"已登录"时用生产存储写入一条经纪, 不伪造凭据读取。 */
    private sealed class TestHome : IDisposable
    {
        public TestHome(string settings, bool signedIn)
        {
            Root = Path.Combine(Path.GetTempPath(), $"dsh-account-route-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "settings.yaml"), settings);
            Home = new HarnessHome(Root);
            if (signedIn)
                new AccountStore(Home).WriteGrant(new AccountGrant(1, "account-token", PlatformOrigin(settings)));
        }

        public string Root { get; }

        public HarnessHome Home { get; }

        public void Dispose() => Directory.Delete(Root, true);

        private static string PlatformOrigin(string settings)
            => settings.Split('\n')
                .Select(line => line.Trim())
                .First(line => line.StartsWith("platformOrigin:", StringComparison.Ordinal))["platformOrigin:".Length..]
                .Trim();
    }

    private sealed record RecordedRequest(string Path, IReadOnlyDictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }

    /** 真实 AgentRegistry/AgentLoop: 用一条用户消息驱动一轮, 观察最终落到哪个模型上。 */
    private sealed class AgentFixture : IAsyncDisposable
    {
        private readonly AgentRegistry _agents;

        private AgentFixture(AgentRegistry agents) => _agents = agents;

        public static Task<AgentFixture> StartAsync(Context ctx)
        {
            _ = ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false) ?? new AgentRegistry(ctx);
            _ = ctx.Get<AgentLoop>(AgentLoop.ServiceName, false) ?? new AgentLoop(ctx);
            return Task.FromResult(new AgentFixture(ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!));
        }

        /** 跑一轮并返回最终请求头里的模型; 失败由调用方从异常里看。 */
        public async Task<string> RunTurnAsync(CancellationToken cancellationToken)
            => await RunTurnAsync(new AgentOptions(), cancellationToken);

        public async Task<string> RunTurnAsync(AgentOptions options, CancellationToken cancellationToken)
        {
            var handle = await _agents.Create(new CreateAgentOptions(AgentOptions: options), cancellationToken);
            using var detach = handle.Dispose;
            handle.Agent.Send(MessageFactory.CreateUserText("hi", new PluginMessageSource("test")), InboxTargets.NextTurn, wakeup: true);
            await handle.Agent.WhenIdle();
            return handle.Agent.Session.RequestHeader()?.Config.Model ?? "";
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /** 只认 GET /models 与 POST /chat/completions 的回环端点, 并记录每个请求的头。 */
    private sealed class AccountEndpoint : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly HttpStatusCode _chatStatus;

        private AccountEndpoint(HttpStatusCode chatStatus)
        {
            _chatStatus = chatStatus;
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            _application = builder.Build();
            _application.Run(Serve);
        }

        public static async Task<AccountEndpoint> StartAsync(HttpStatusCode chatStatus = HttpStatusCode.OK)
        {
            var endpoint = new AccountEndpoint(chatStatus);
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

        public List<RecordedRequest> Requests { get; } = [];

        private async Task Serve(HttpContext context)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in context.Request.Headers)
                headers[header.Key] = header.Value.ToString();
            var path = context.Request.Path.Value ?? "";
            Requests.Add(new RecordedRequest(path, headers));
            if (path.EndsWith("/models", StringComparison.Ordinal))
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(ListingJson, context.RequestAborted);
                return;
            }
            if (!path.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (_chatStatus != HttpStatusCode.OK)
            {
                context.Response.StatusCode = (int)_chatStatus;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("""{"error":{"message":"invalid dsh token"}}""", context.RequestAborted);
                return;
            }
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(Sse, context.RequestAborted);
        }

        public ValueTask DisposeAsync() => _application.DisposeAsync();
    }
}