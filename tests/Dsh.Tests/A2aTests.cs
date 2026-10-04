using System.Net;
using System.Text;
using Dsh.A2A;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
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

    private sealed class MockDeepSeekServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly IReadOnlyList<string> _script;
        private readonly TimeSpan _responseDelay;

        public MockDeepSeekServer(IReadOnlyList<string> script, TimeSpan? responseDelay = null)
        {
            _script = script;
            _responseDelay = responseDelay ?? TimeSpan.Zero;
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _ = Task.Run(Serve);
        }

        public int Port { get; }

        public string BaseUrl => $"http://127.0.0.1:{Port}";

        private async Task Serve()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                }
                catch (Exception)
                {
                    break;
                }
                try
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    _ = await reader.ReadToEndAsync();
                    if (_responseDelay > TimeSpan.Zero)
                        await Task.Delay(_responseDelay, _stop.Token);
                    context.Response.ContentType = "text/event-stream";
                    foreach (var line in _script)
                    {
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes($"{line}\n"));
                        await context.Response.OutputStream.FlushAsync();
                    }
                    context.Response.Close();
                }
                catch (Exception)
                {
                    break;
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    private sealed class A2aFixture : IDisposable
    {
        private readonly DshAgentHandler _handler;
        private readonly A2aHost _host;

        public A2aFixture(string llmBaseUrl)
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
            _host = new A2aHost(_handler, new A2aHostOptions());
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

    [Fact]
    public async Task AgentCard_Resolves()
    {
        using var server = new MockDeepSeekServer(SseTextOnly);
        using var fixture = new A2aFixture(server.BaseUrl);
        var card = await new A2ACardResolver(new Uri(fixture.Endpoint + "/")).GetAgentCardAsync(TestContext.Current.CancellationToken);
        Assert.Equal("deepseek-harness", card.Name);
        Assert.True(card.Capabilities!.Streaming);
        Assert.Equal("coding", card.Skills![0].Id);
    }

    [Fact]
    public async Task SendMessage_Completes_WithFinalText()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var server = new MockDeepSeekServer(SseTextOnly);
        using var fixture = new A2aFixture(server.BaseUrl);
        using var client = new A2AClient(new Uri(fixture.Endpoint));

        var response = await client.SendMessageAsync(new SendMessageRequest { Message = UserMessage("hi") }, cancellationToken);
        var taskId = response.Task!.Id;
        var task = await WaitTerminalAsync(client, taskId, cancellationToken);

        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal("Hello world", task.Status.Message!.Parts[0].Text);
    }

    [Fact]
    public async Task SendStreaming_EmitsWorkingThenCompleted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var server = new MockDeepSeekServer(SseTextOnly);
        using var fixture = new A2aFixture(server.BaseUrl);
        using var client = new A2AClient(new Uri(fixture.Endpoint));

        var frames = new List<global::A2A.StreamResponse>();
        await foreach (var frame in client.SendStreamingMessageAsync(new SendMessageRequest { Message = UserMessage("hi") }, cancellationToken))
            frames.Add(frame);

        Assert.Contains(frames, frame => frame.StatusUpdate?.Status.State == TaskState.Working);
        Assert.Equal(TaskState.Completed, frames[^1].StatusUpdate!.Status.State);
    }

    [Fact]
    public async Task Cancel_RunningTask_ThenNotCancelable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var server = new MockDeepSeekServer(SseTextOnly, TimeSpan.FromSeconds(5));
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
        using var server = new MockDeepSeekServer(SseTextOnly);
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
    public async Task RemoteClient_Cancel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var server = new MockDeepSeekServer(SseTextOnly, TimeSpan.FromSeconds(5));
        using var fixture = new A2aFixture(server.BaseUrl);
        var client = new A2aRemoteClient(new Dictionary<string, A2aRemoteSettings>());

        var sent = await client.SendAsync(fixture.Endpoint, "hi", wait: false, timeoutSeconds: 1, cancellationToken);
        Assert.NotNull(sent.Task);
        var canceled = await client.CancelAsync(fixture.Endpoint, sent.Task!.Id, cancellationToken);
        Assert.Equal(nameof(TaskState.Canceled), canceled.Task!.State);
    }
}
