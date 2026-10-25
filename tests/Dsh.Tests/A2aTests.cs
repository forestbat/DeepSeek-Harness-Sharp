using System.Net;
using System.Text;
using System.Net.Sockets;
using Dsh.A2A;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using A2AClient = global::A2A.A2AClient;
using A2ACardResolver = global::A2A.A2ACardResolver;
using A2AMessage = global::A2A.Message;
using A2APart = global::A2A.Part;
using A2ARole = global::A2A.Role;
using CancelTaskRequest = global::A2A.CancelTaskRequest;
using GetTaskRequest = global::A2A.GetTaskRequest;
using SendMessageRequest = global::A2A.SendMessageRequest;
using SendMessageConfiguration = global::A2A.SendMessageConfiguration;
using TaskState = global::A2A.TaskState;

namespace Dsh.Tests;

/** 用官方 SDK 客户端(A2AClient/A2ACardResolver)驱动 Dsh.A2A 宿主, 端到端验证协议语义。 */
public sealed class A2aTests
{
    private static readonly string[] SseTextOnly =
    [
        """data: {"choices":[{"delta":{"role":"assistant","content":null}}]}""",
        "",
        """data: {"choices":[{"delta":{"content":"Hello"}}]}""",
        "",
        """data: {"choices":[{"delta":{"content":" world"}}]}""",
        "",
        """data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}""",
        "",
        "data: [DONE]",
        "",
    ];

    private sealed class MockDeepSeekServer : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly IReadOnlyList<string> _script;
        private readonly Task? _responseGate;

        private MockDeepSeekServer(IReadOnlyList<string> script, Task? responseGate)
        {
            _script = script;
            _responseGate = responseGate;
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            _application = builder.Build();
            _application.Run(Serve);
        }

        public static async Task<MockDeepSeekServer> StartAsync(IReadOnlyList<string> script, Task? responseGate = null)
        {
            var server = new MockDeepSeekServer(script, responseGate);
            try
            {
                await server._application.StartAsync(TestContext.Current.CancellationToken);
                server.Port = new Uri(server._application.Urls.Single()).Port;
                return server;
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        public int Port { get; private set; }

        public string BaseUrl => $"http://127.0.0.1:{Port}";

        private async Task Serve(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body);
            _ = await reader.ReadToEndAsync(context.RequestAborted);
            if (_responseGate is not null)
                await _responseGate.WaitAsync(context.RequestAborted);
            context.Response.ContentType = "text/event-stream";
            foreach (var line in _script)
            {
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"{line}\n"), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }

        public ValueTask DisposeAsync() => _application.DisposeAsync();
    }

    private sealed class A2aFixture : IDisposable
    {
        private readonly DshAgentHandler _handler;
        private readonly A2aHost _host;

        public A2aFixture(string llmBaseUrl, string? authToken = null, A2aHostOptions? options = null)
        {
            Ctx = new Context();
            _ = new SessionStore(Ctx);
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            _ = new ToolRuntime(Ctx);
            var llm = new LlmRuntime(Ctx);
            _ = new AgentRegistry(Ctx);
            _ = new AgentLoop(Ctx);
            var connection = new Dsh.Llm.DeepSeek.DeepSeekConnectionOptions(
                llmBaseUrl,
                "TEST_KEY",
                new Dsh.Llm.DeepSeek.RequestDefaults(),
                Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultMaxTokens,
                Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultContextWindowValue,
                [new Dsh.Llm.DeepSeek.DeepSeekCatalogModel("test-model")],
                Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultStreamIdleTimeoutMs,
                ResolvedRetryPolicy.Resolve(null, "test"));
            var adapter = new Dsh.Llm.DeepSeek.DeepSeekAdapter("test-provider", new Dsh.Llm.DeepSeek.DeepSeekAdapterOptions
            {
                Options = () => connection,
                ResolveApiKey = (_, _) => Task.FromResult("sk-test"),
                ResolveUserId = () => "test-user",
            });
            llm.RegisterAdapter(["test-provider"], adapter);
            _handler = new DshAgentHandler(Ctx, "test-provider", "test-model");
            _host = new A2aHost(_handler, options ?? new A2aHostOptions(AuthToken: authToken));
            _host.Start();
        }

        public Context Ctx { get; }

        public string Endpoint => _host.Endpoint;

        public void Dispose()
        {
            _host.Dispose();
            _handler.Dispose();
        }
    }

    private static A2AMessage UserMessage(string text)
        => new()
        {
            MessageId = Guid.NewGuid().ToString("N"),
            Role = A2ARole.User,
            Parts = [A2APart.FromText(text)],
        };

    private static async Task<global::A2A.AgentTask> WaitTerminalAsync(A2AClient client, string taskId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var task = await client.GetTaskAsync(new GetTaskRequest { Id = taskId }, cancellationToken);
            if (task.Status.State is TaskState.Completed or TaskState.Failed or TaskState.Canceled)
                return task;
            await Task.Delay(50, cancellationToken);
        }
        throw new TimeoutException($"task {taskId} did not reach a terminal state");
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("[::1]")]
    public async Task AgentCard_Resolves(string host)
    {
        await using var server = await MockDeepSeekServer.StartAsync(SseTextOnly);
        using var fixture = new A2aFixture(server.BaseUrl, options: new A2aHostOptions(Host: host));
        var card = await new A2ACardResolver(new Uri(fixture.Endpoint + "/")).GetAgentCardAsync(TestContext.Current.CancellationToken);
        Assert.Equal("deepseek-harness", card.Name);
        Assert.True(card.Capabilities!.Streaming);
        Assert.Equal(fixture.Endpoint, Assert.Single(card.SupportedInterfaces).Url);
        Assert.Equal("coding", card.Skills![0].Id);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SendMessage_Completes_WithFinalText(bool chunked, bool expectContinue)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await MockDeepSeekServer.StartAsync(SseTextOnly);
        using var fixture = new A2aFixture(server.BaseUrl);
        using var http = new HttpClient(new RequestFramingHandler(chunked, expectContinue));
        using var client = new A2AClient(new Uri(fixture.Endpoint), http);

        var response = await client.SendMessageAsync(
            new SendMessageRequest { Message = UserMessage(chunked ? new string('x', 8 * 1024) : "hi") }, cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var taskId = response.Task!.Id;
        var task = await WaitTerminalAsync(client, taskId, cancellationToken);

        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal("Hello world", task.Status.Message!.Parts[0].Text);
    }

    [Fact]
    public async Task SendStreaming_EmitsWorkingThenCompleted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await MockDeepSeekServer.StartAsync(SseTextOnly);
        using var fixture = new A2aFixture(server.BaseUrl);
        using var client = new A2AClient(new Uri(fixture.Endpoint));

        var frames = new List<global::A2A.StreamResponse>();
        await foreach (var frame in client.SendStreamingMessageAsync(new SendMessageRequest { Message = UserMessage("hi") }, cancellationToken))
            frames.Add(frame);

        Assert.Contains(frames, frame => frame.StatusUpdate?.Status.State == TaskState.Working);
        Assert.Equal(TaskState.Completed, frames[^1].StatusUpdate!.Status.State);
    }

    [Fact]
    public async Task SendStreaming_CanCancelWhileAgentIsWorking()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var responseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await MockDeepSeekServer.StartAsync(SseTextOnly, responseGate.Task);
        using var fixture = new A2aFixture(server.BaseUrl);
        using var client = new A2AClient(new Uri(fixture.Endpoint));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var events = client.SendStreamingMessageAsync(
            new SendMessageRequest { Message = UserMessage("hi") }, stop.Token).GetAsyncEnumerator(stop.Token);

        try
        {
            var working = false;
            while (await events.MoveNextAsync())
            {
                if (events.Current.StatusUpdate?.Status.State != TaskState.Working)
                    continue;
                working = true;
                break;
            }
            Assert.True(working);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                while (await events.MoveNextAsync())
                {
                }
            });
        }
        finally
        {
            responseGate.TrySetResult();
        }
    }

    [Fact]
    public async Task Cancel_RunningTask_ThenNotCancelable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var responseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await MockDeepSeekServer.StartAsync(SseTextOnly, responseGate.Task);
        using var fixture = new A2aFixture(server.BaseUrl);
        using var client = new A2AClient(new Uri(fixture.Endpoint));

        var response = await client.SendMessageAsync(new SendMessageRequest
        {
            Message = UserMessage("hi"),
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        }, cancellationToken);
        var taskId = response.Task!.Id;
        var canceled = await client.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, cancellationToken);
        Assert.Equal(TaskState.Canceled, canceled.Status.State);
        await Assert.ThrowsAsync<global::A2A.A2AException>(() => client.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, cancellationToken));
    }

    [Fact]
    public async Task RemoteClient_CardSendGetList()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await MockDeepSeekServer.StartAsync(SseTextOnly);
        using var fixture = new A2aFixture(server.BaseUrl);
        var client = new A2aRemoteClient(new Dictionary<string, A2aRemoteSettings>());

        var card = await client.CardAsync(fixture.Endpoint, cancellationToken);
        Assert.True(card.Ok);
        Assert.Equal("deepseek-harness", card.Name);

        var sent = await client.SendAsync(fixture.Endpoint, "hi", wait: true, timeoutSeconds: 20, cancellationToken);
        Assert.NotNull(sent.Task);
        Assert.Equal(nameof(TaskState.Completed), sent.Task!.State);
        Assert.Equal("Hello world", sent.Task.Answer);

        var got = await client.GetAsync(fixture.Endpoint, sent.Task.Id, cancellationToken);
        Assert.Equal(nameof(TaskState.Completed), got.Task!.State);

        var list = await client.ListAsync(fixture.Endpoint, null, 50, cancellationToken);
        Assert.Contains(list.Tasks, task => task.Id == sent.Task.Id);
    }

    [Fact]
    public async Task Host_WithAuthToken_RejectsMissingOrWrongBearer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await MockDeepSeekServer.StartAsync(SseTextOnly);
        using var fixture = new A2aFixture(server.BaseUrl, AuthToken);
        using var http = new HttpClient();

        using var anonymousCard = await http.GetAsync($"{fixture.Endpoint}/.well-known/agent-card.json", cancellationToken);
        using var anonymousRpc = await http.PostAsync(fixture.Endpoint, new StringContent(ListTasksRpc, Encoding.UTF8, "application/json"), cancellationToken);
        using var wrongRequest = new HttpRequestMessage(HttpMethod.Post, fixture.Endpoint)
        {
            Content = new StringContent(ListTasksRpc, Encoding.UTF8, "application/json"),
        };
        wrongRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $"{AuthToken}x");
        using var wrongRpc = await http.SendAsync(wrongRequest, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousCard.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRpc.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongRpc.StatusCode);
    }

    [Fact]
    public async Task Host_WithAuthToken_AcceptsMatchingBearer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await MockDeepSeekServer.StartAsync(SseTextOnly);
        using var fixture = new A2aFixture(server.BaseUrl, AuthToken);
        var client = new A2aRemoteClient(new Dictionary<string, A2aRemoteSettings>());
        var target = $"{fixture.Endpoint}?token={AuthToken}";

        var card = await client.CardAsync(target, cancellationToken);
        var sent = await client.SendAsync(target, "hi", wait: true, timeoutSeconds: 20, cancellationToken);

        Assert.Equal("deepseek-harness", card.Name);
        Assert.Equal("Hello world", sent.Task!.Answer);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("+")]
    [InlineData("*")]
    [InlineData("192.168.1.10")]
    public void Host_WithoutAuthToken_RefusesNonLoopbackBind(string host)
    {
        var ctx = new Context();
        using var handler = new DshAgentHandler(ctx, "test-provider", "test-model");

        Assert.Throws<InvalidOperationException>(() => new A2aHost(handler, new A2aHostOptions(host)));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.2")]
    [InlineData("[::1]")]
    public void Host_WithoutAuthToken_AllowsLoopbackBind(string host)
    {
        var ctx = new Context();
        using var handler = new DshAgentHandler(ctx, "test-provider", "test-model");

        using var a2aHost = new A2aHost(handler, new A2aHostOptions(host));

        Assert.Contains(host, a2aHost.Endpoint);
    }

    private const string AuthToken = "a2a-test-token";

    private const string ListTasksRpc = """{"jsonrpc":"2.0","id":1,"method":"ListTasks","params":{}}""";

    [Fact]
    public async Task RemoteClient_Cancel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var responseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await MockDeepSeekServer.StartAsync(SseTextOnly, responseGate.Task);
        using var fixture = new A2aFixture(server.BaseUrl);
        var client = new A2aRemoteClient(new Dictionary<string, A2aRemoteSettings>());

        var sent = await client.SendAsync(fixture.Endpoint, "hi", wait: false, timeoutSeconds: 1, cancellationToken);
        Assert.NotNull(sent.Task);
        var canceled = await client.CancelAsync(fixture.Endpoint, sent.Task!.Id, cancellationToken);
        Assert.Equal(nameof(TaskState.Canceled), canceled.Task!.State);
    }

    private sealed class RequestFramingHandler(bool chunked, bool expectContinue) : DelegatingHandler(
        new SocketsHttpHandler { Expect100ContinueTimeout = Timeout.InfiniteTimeSpan })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (expectContinue && request.Content is not null)
                request.Headers.ExpectContinue = true;
            if (chunked && request.Content is not null)
            {
                request.Headers.TransferEncodingChunked = true;
                request.Content.Headers.ContentLength = null;
            }
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task SendMessage_ConcurrentHosts_KeepEndpointsCardsAndResultsIsolated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var endpoints = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            try
            {
                var marker = $"host-{index}";
                var script = SseTextOnly.Select(line => line.Replace("Hello", marker, StringComparison.Ordinal)).ToArray();
                await using var server = await MockDeepSeekServer.StartAsync(script);
                using var fixture = new A2aFixture(server.BaseUrl);
                using var client = new A2AClient(new Uri(fixture.Endpoint));
                if (Interlocked.Increment(ref started) == 8)
                    allStarted.TrySetResult();
                await allStarted.Task.WaitAsync(cancellationToken);

                var card = await new A2ACardResolver(new Uri(fixture.Endpoint + "/")).GetAgentCardAsync(cancellationToken);
                Assert.NotEqual(0, new Uri(fixture.Endpoint).Port);
                Assert.Equal(fixture.Endpoint, Assert.Single(card.SupportedInterfaces).Url);
                var response = await client.SendMessageAsync(new SendMessageRequest { Message = UserMessage(marker) }, cancellationToken);
                var task = await WaitTerminalAsync(client, response.Task!.Id, cancellationToken);
                Assert.Equal(TaskState.Completed, task.Status.State);
                Assert.Equal($"{marker} world", task.Status.Message!.Parts[0].Text);
                return fixture.Endpoint;
            }
            catch (Exception error)
            {
                allStarted.TrySetException(error);
                throw;
            }
        }));

        Assert.Equal(endpoints.Length, endpoints.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Host_PublicUrl_RemainsInCard_AndDisposeClosesListener()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string publicUrl = "https://agent.example/a2a";
        await using var server = await MockDeepSeekServer.StartAsync(SseTextOnly);
        var fixture = new A2aFixture(server.BaseUrl, options: new A2aHostOptions(PublicUrl: publicUrl));
        string endpoint;
        try
        {
            endpoint = fixture.Endpoint;
            var card = await new A2ACardResolver(new Uri(endpoint + "/")).GetAgentCardAsync(cancellationToken);
            Assert.Equal(publicUrl, Assert.Single(card.SupportedInterfaces).Url);
            using var client = new A2AClient(new Uri(endpoint));
            var response = await client.SendMessageAsync(new SendMessageRequest { Message = UserMessage("before close") }, cancellationToken);
            var task = await WaitTerminalAsync(client, response.Task!.Id, cancellationToken);
            Assert.Equal(TaskState.Completed, task.Status.State);
            Assert.Equal("Hello world", task.Status.Message!.Parts[0].Text);
        }
        finally
        {
            fixture.Dispose();
        }

        await AssertListenerEventuallyClosedAsync(new Uri(endpoint).Port, cancellationToken);
    }

    /** The Kestrel listener can finish closing just after Dispose returns; observe the port until the connection is refused. */
    private static async Task AssertListenerEventuallyClosedAsync(int port, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            }
            catch (SocketException)
            {
                return;
            }
            if (DateTime.UtcNow >= deadline)
                Assert.Fail($"listener on loopback:{port} remained reachable after dispose");
            await Task.Delay(50, cancellationToken);
        }
    }

    [Fact]
    public async Task Host_RejectsUnexpectedHostHeader()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await MockDeepSeekServer.StartAsync(SseTextOnly);
        using var fixture = new A2aFixture(server.BaseUrl);
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, fixture.Endpoint)
        {
            Content = new StringContent(ListTasksRpc, Encoding.UTF8, "application/json"),
        };
        request.Headers.Host = $"untrusted.example:{new Uri(fixture.Endpoint).Port}";
        using var response = await http.SendAsync(request, cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
