using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Dsh.Runtime;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Tools;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Dsh.Tests;

public class AgentLoopIntegrationTests
{
    private const string TextOnlyModel = "text-only-model";

    private static readonly string[] SseTextOnly =
    [
        """data: {"choices":[{"delta":{"role":"assistant","content":null,"reasoning_content":""}}]}""",
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
        private readonly Func<string, IReadOnlyList<string>> _script;

        private MockDeepSeekServer(Func<string, IReadOnlyList<string>> script)
        {
            _script = script;
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            _application = builder.Build();
            _application.Run(Serve);
        }

        public static async Task<MockDeepSeekServer> StartAsync(Func<string, IReadOnlyList<string>> script)
        {
            var server = new MockDeepSeekServer(script);
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

        public string? LastRequestBody { get; private set; }

        private async Task Serve(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body);
            LastRequestBody = await reader.ReadToEndAsync(context.RequestAborted);
            context.Response.ContentType = "text/event-stream";
            foreach (var line in _script(LastRequestBody))
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }

        public ValueTask DisposeAsync() => _application.DisposeAsync();
    }

    private sealed class HarnessFixture : IDisposable
    {
        public Context Ctx { get; }
        public SessionStore Sessions { get; }
        public ToolRuntime Tools { get; }
        public AgentRegistry Agents { get; }
        public AgentLoop Loop { get; }

        public HarnessFixture(string baseUrl, ResolvedRetryPolicy? retryPolicy = null, AgentLoopConfig? loopConfig = null, IReadOnlyList<string>? inputModalities = null)
        {
            Ctx = new Context();
            Sessions = new SessionStore(Ctx);
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            var llm = new LlmRuntime(Ctx);
            Agents = new AgentRegistry(Ctx);
            Loop = new AgentLoop(Ctx, loopConfig);
            var connection = new Dsh.Llm.DeepSeek.DeepSeekConnectionOptions(
                baseUrl,
                "TEST_KEY",
                new Dsh.Llm.DeepSeek.RequestDefaults(),
                Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultMaxTokens,
                Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultContextWindowValue,
                [new Dsh.Llm.DeepSeek.DeepSeekCatalogModel(
                    "test-model",
                    InputModalities: inputModalities,
                    SystemPromptUpdate: SystemPromptUpdateModes.InHistory),
                    new Dsh.Llm.DeepSeek.DeepSeekCatalogModel(TextOnlyModel)],
                Dsh.Llm.DeepSeek.DeepSeekConnectionOptions.DefaultStreamIdleTimeoutMs,
                retryPolicy ?? ResolvedRetryPolicy.Resolve(null, "test"));
            var adapter = new Dsh.Llm.DeepSeek.DeepSeekAdapter("test-provider", new Dsh.Llm.DeepSeek.DeepSeekAdapterOptions
            {
                Options = () => connection,
                ResolveApiKey = (_, _) => Task.FromResult("sk-test"),
                ResolveUserId = () => "test-user",
            });
            llm.RegisterAdapter(["test-provider"], adapter);
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task TextTurn_LogsFullEventSequence()
    {
        await using var server = await MockDeepSeekServer.StartAsync(_ => SseTextOnly);
        using var fixture = new HarnessFixture(server.BaseUrl);
        var handle = await fixture.Agents.Create(new CreateAgentOptions(
            SessionId.Create("session-test-1"),
            null,
            new AgentOptions("test-provider", "test-model")), TestContext.Current.CancellationToken);
        var agent = (AgentLoopAgent)handle.Agent;

        agent.Followup(MessageFactory.CreateUserText("hi"));
        await agent.WhenIdle();

        var types = agent.Session.SnapshotEvents().Select(e => e.Type).Where(t => t != SessionEventTypes.AgentInboxSpliced).ToList();
        Assert.Equal(
            [
                SessionEventTypes.TurnStart,
                SessionEventTypes.StepStart,
                SessionEventTypes.SystemMessage,
                SessionEventTypes.UserMessage,
                SessionEventTypes.RequestHeader,
                SessionEventTypes.RequestContext,
                .. Enumerable.Repeat(SessionEventTypes.AssistantChunk, 6),
                SessionEventTypes.AssistantMessage,
                SessionEventTypes.StepEnd,
                SessionEventTypes.TurnEnd,
            ],
            types);

        var turnEnd = agent.Session.SnapshotEvents().Select(e => e.Data).OfType<TurnEndPayload>().Single();
        Assert.IsType<TurnEndReason.Completed>(turnEnd.Reason);

        var assistant = agent.Session.SnapshotEvents().Select(e => e.Data).OfType<AssistantMessagePayload>().Single();
        Assert.Equal("Hello world", string.Concat(assistant.Message.Content.OfType<TextBlock>().Select(b => b.Text)));
        Assert.Equal(new TokenUsage(10, 2, 12), assistant.Usage);

        var messages = agent.Session.DeriveMessages();
        Assert.Equal(3, messages.Count);
        Assert.Equal(MessageRole.System, messages[0].Role);
        Assert.Contains("test-model", server.LastRequestBody);
        Assert.Contains("environment_details", server.LastRequestBody);
        Assert.Contains("\"stream\":true", server.LastRequestBody);
    }

    [Fact]
    public async Task ToolCallTurn_ExecutesAndLoops()
    {
        var calls = 0;
        var scriptCalls = 0;
        await using var server = await MockDeepSeekServer.StartAsync(_ =>
        {
            scriptCalls++;
            if (scriptCalls == 1)
            {
                return
                [
                    "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":null}}]}",
                    "",
                    "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"echo\",\"arguments\":\"{\\\"text\\\":\\\"abc\\\"}\"}}]}}]}",
                    "",
                    "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}",
                    "",
                    "data: [DONE]",
                    "",
                ];
            }
            return
            [
                """data: {"choices":[{"delta":{"content":"done"}}]}""",
                "",
                """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""",
                "",
                "data: [DONE]",
                "",
            ];
        });
        using var fixture = new HarnessFixture(server.BaseUrl);
        fixture.Tools.Register(new ToolDefinition
        {
            Name = "echo",
            Description = "echo tool",
            Parameters = new System.Text.Json.Nodes.JsonObject(),
            Output = new ToolOutputDefinition(
                new System.Text.Json.Nodes.JsonObject(),
                (_, value) => [new TextBlock(value.GetProperty("echo").GetString()!)]),
            Execute = (args, _) =>
            {
                calls++;
                var echoed = args.GetProperty("text").GetString()!;
                return Task.FromResult<object?>(new { echo = echoed });
            },
            IsConcurrencySafe = _ => true,
        });
        var handle = await fixture.Agents.Create(new CreateAgentOptions(
            SessionId.Create("session-test-2"),
            null,
            new AgentOptions("test-provider", "test-model")), TestContext.Current.CancellationToken);
        var agent = (AgentLoopAgent)handle.Agent;

        agent.Followup(MessageFactory.CreateUserText("echo please"));
        await agent.WhenIdle();

        Assert.Equal(1, calls);
        Assert.Equal(2, scriptCalls);
        var types = agent.Session.SnapshotEvents().Select(e => e.Type).ToList();
        Assert.Contains(SessionEventTypes.ToolCall, types);
        Assert.Contains(SessionEventTypes.ToolResult, types);
        var toolResult = agent.Session.SnapshotEvents().Select(e => e.Data).OfType<ToolResultPayload>().Single();
        Assert.False(toolResult.Message.Block.IsError);
        Assert.Equal("abc", Assert.IsType<TextBlock>(toolResult.Message.Block.Content[0]).Text);
        var turnEnd = agent.Session.SnapshotEvents().Select(e => e.Data).OfType<TurnEndPayload>().Single();
        Assert.IsType<TurnEndReason.Completed>(turnEnd.Reason);
    }

    [Fact]
    public async Task ParallelToolCalls_RespectConfiguredLimit()
    {
        var scriptCalls = 0;
        await using var server = await MockDeepSeekServer.StartAsync(_ =>
        {
            scriptCalls++;
            if (scriptCalls > 1)
            {
                return
                [
                    """data: {"choices":[{"delta":{"content":"done"}}]}""",
                    "",
                    """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""",
                    "",
                    "data: [DONE]",
                    "",
                ];
            }
            var calls = Enumerable.Range(0, 3).Select(index =>
                $$$"""{"index":{{{index}}},"id":"call_{{{index}}}","type":"function","function":{"name":"probe","arguments":"{}"}}""");
            return
            [
                $$$"""data: {"choices":[{"delta":{"role":"assistant","content":null,"tool_calls":[{{{string.Join(",", calls)}}}]}}]}""",
                "",
                """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
                "",
                "data: [DONE]",
                "",
            ];
        });
        using var fixture = new HarnessFixture(server.BaseUrl, loopConfig: new AgentLoopConfig { MaxParallelToolCalls = 1 });
        var running = 0;
        var peak = 0;
        fixture.Tools.Register(new ToolDefinition
        {
            Name = "probe",
            Description = "records how many probe calls overlap",
            Parameters = new System.Text.Json.Nodes.JsonObject(),
            Output = new ToolOutputDefinition(new System.Text.Json.Nodes.JsonObject(), (_, _) => [new TextBlock("ok")]),
            Execute = async (_, _) =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref peak, now);
                await Task.Delay(100);
                Interlocked.Decrement(ref running);
                return new { };
            },
            IsConcurrencySafe = _ => true,
        });
        var handle = await fixture.Agents.Create(new CreateAgentOptions(
            SessionId.Create("session-parallel-limit"),
            null,
            new AgentOptions("test-provider", "test-model")), TestContext.Current.CancellationToken);
        var agent = (AgentLoopAgent)handle.Agent;

        agent.Followup(MessageFactory.CreateUserText("probe three times"));
        await agent.WhenIdle();

        Assert.Equal(3, agent.Session.SnapshotEvents().Select(e => e.Data).OfType<ToolResultPayload>().Count());
        Assert.Equal(1, peak);
    }

    /**
     * 并发调用里有一个抛异常时，其余调用必须照常结算，成果不能一起丢掉。
     *
     * 现状: ToolRuntime 在 Prepare/Dispatch/Finalize 三层各自把异常转成单次调用的 Failure，
     * 所以异常本身是隔离的；真正会把整批拖垮的是调度器 catch 到的异常(RunGroup 的 schedulerFailure)。
     * 这条用例钉的是"异常隔离"这个对外可见契约，实现细节不在测试里复制。
     */
    [Fact]
    public async Task ParallelToolCalls_OneThrowingCallDoesNotDiscardTheOthers()
    {
        var scriptCalls = 0;
        await using var server = await MockDeepSeekServer.StartAsync(_ =>
        {
            scriptCalls++;
            if (scriptCalls > 1)
            {
                return
                [
                    """data: {"choices":[{"delta":{"content":"done"}}]}""",
                    "",
                    """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""",
                    "",
                    "data: [DONE]",
                    "",
                ];
            }
            var calls = Enumerable.Range(0, 3).Select(index =>
                $$$"""{"index":{{{index}}},"id":"call_{{{index}}}","type":"function","function":{"name":"probe","arguments":"{\"index\":{{{index}}}}"}}""");
            return
            [
                $$$"""data: {"choices":[{"delta":{"role":"assistant","content":null,"tool_calls":[{{{string.Join(",", calls)}}}]}}]}""",
                "",
                """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
                "",
                "data: [DONE]",
                "",
            ];
        });
        using var fixture = new HarnessFixture(server.BaseUrl);
        var completed = new System.Collections.Concurrent.ConcurrentBag<int>();
        fixture.Tools.Register(new ToolDefinition
        {
            Name = "probe",
            Description = "throws for index 0, succeeds otherwise",
            Parameters = new System.Text.Json.Nodes.JsonObject(),
            Output = new ToolOutputDefinition(new System.Text.Json.Nodes.JsonObject(), (_, _) => [new TextBlock("ok")]),
            Execute = async (args, _) =>
            {
                var index = args.GetProperty("index").GetInt32();
                if (index == 0)
                    throw new InvalidOperationException("probe blew up on purpose");
                await Task.Delay(50);
                completed.Add(index);
                return new { };
            },
            IsConcurrencySafe = _ => true,
        });
        var handle = await fixture.Agents.Create(new CreateAgentOptions(
            SessionId.Create("session-parallel-isolation"),
            null,
            new AgentOptions("test-provider", "test-model")), TestContext.Current.CancellationToken);
        var agent = (AgentLoopAgent)handle.Agent;

        agent.Followup(MessageFactory.CreateUserText("probe three times"));
        await agent.WhenIdle();

        var results = agent.Session.SnapshotEvents().Select(e => e.Data).OfType<ToolResultPayload>().ToList();
        Assert.Equal(3, results.Count);
        Assert.Single(results, result => result.Message.Block.IsError == true);
        Assert.Equal(2, results.Count(result => result.Message.Block.IsError != true));
        // 幸存的两次调用真的跑到了 Execute，而不是被"跳过"占位。
        Assert.Equal([1, 2], completed.Order());
        Assert.IsType<TurnEndReason.Completed>(
            agent.Session.SnapshotEvents().Select(e => e.Data).OfType<TurnEndPayload>().Single().Reason);
    }

    /**
     * 用户按停/父会话连坐/进程销毁 必须能区分开: 回合结算写的是真实取消原因, 不是一律 User。
     *
     * 旧实现把 Cancel 的 cause 丢掉, 结算时硬编码 User, 于是界面上永远显示"中止 · user",
     * 连"子代理是被父会话掐掉的"都看不出来。这里用一个卡住的工具把取消点固定在回合中间。
     */
    [Fact]
    public async Task CancelledTurn_RecordsWhoCancelledIt()
    {
        await using var server = await MockDeepSeekServer.StartAsync(_ =>
        [
            """data: {"choices":[{"delta":{"role":"assistant","content":null,"tool_calls":[{"index":0,"id":"call_block","type":"function","function":{"name":"block","arguments":"{}"}}]}}]}""",
            "",
            """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            "",
            "data: [DONE]",
            "",
        ]);
        using var fixture = new HarnessFixture(server.BaseUrl);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Tools.Register(new ToolDefinition
        {
            Name = "block",
            Description = "blocks until the test releases it",
            Parameters = new System.Text.Json.Nodes.JsonObject(),
            Output = new ToolOutputDefinition(new System.Text.Json.Nodes.JsonObject(), (_, _) => [new TextBlock("ok")]),
            Execute = async (_, runContext) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(runContext.Signal);
                return new { };
            },
            IsConcurrencySafe = _ => true,
        });
        var handle = await fixture.Agents.Create(new CreateAgentOptions(
            SessionId.Create("session-cancel-cause"),
            null,
            new AgentOptions("test-provider", "test-model")), TestContext.Current.CancellationToken);
        var agent = (AgentLoopAgent)handle.Agent;

        agent.Followup(MessageFactory.CreateUserText("run the blocking tool"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        agent.Cancel(new AgentCancelCause.Hook("cancel-reason-probe"));
        release.TrySetResult();
        await agent.WhenIdle();

        var reason = agent.Session.SnapshotEvents()
            .Select(sessionEvent => sessionEvent.Data)
            .OfType<TurnEndPayload>()
            .Single()
            .Reason;
        var aborted = Assert.IsType<TurnEndReason.Aborted>(reason);
        var cause = Assert.IsType<AgentCancelCause.Hook>(aborted.Reason);
        Assert.Equal("cancel-reason-probe", cause.Reason);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    [Fact]
    public async Task TruncatedStream_RetriesThenErrorsInsteadOfCompleting()
    {
        var calls = 0;
        await using var server = await MockDeepSeekServer.StartAsync(_ =>
        {
            calls++;
            // 有内容但流尾缺 [DONE]/finish_reason: 供应商中途断开。
            return
            [
                """data: {"choices":[{"delta":{"content":"partial"}}]}""",
                "",
            ];
        });
        using var fixture = new HarnessFixture(server.BaseUrl, retryPolicy: ResolvedRetryPolicy.Resolve(
            new RetryPolicyConfig.Normal(
                MaxRetries: 1,
                Backoff: new BackoffConfig(InitialDelayMs: 1, MaxDelayMs: 1, JitterRatio: 0)),
            "test"));
        var handle = await fixture.Agents.Create(new CreateAgentOptions(
            SessionId.Create("session-truncated"),
            null,
            new AgentOptions("test-provider", "test-model")), TestContext.Current.CancellationToken);
        var agent = (AgentLoopAgent)handle.Agent;

        agent.Followup(MessageFactory.CreateUserText("hi"));
        await agent.WhenIdle();

        Assert.Equal(2, calls);
        var turnEnd = agent.Session.SnapshotEvents().Select(e => e.Data).OfType<TurnEndPayload>().Single();
        Assert.IsType<TurnEndReason.Error>(turnEnd.Reason);
        var assistant = agent.Session.SnapshotEvents().Select(e => e.Data).OfType<AssistantMessagePayload>().Single();
        Assert.True(assistant.Interrupted);
        Assert.Equal("partial", string.Concat(assistant.Message.Content.OfType<TextBlock>().Select(b => b.Text)));
    }

    [Fact]
    public async Task InHistoryModel_AppendsSystemPromptOnChange()
    {
        await using var server = await MockDeepSeekServer.StartAsync(_ => SseTextOnly);
        using var fixture = new HarnessFixture(server.BaseUrl);
        var handle = await fixture.Agents.Create(new CreateAgentOptions(
            SessionId.Create("session-in-history"),
            null,
            new AgentOptions("test-provider", "test-model")), TestContext.Current.CancellationToken);
        var agent = (AgentLoopAgent)handle.Agent;

        agent.Followup(MessageFactory.CreateUserText("hi"));
        await agent.WhenIdle();
        fixture.Ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!.ReplacePersona("persona v2");
        agent.Followup(MessageFactory.CreateUserText("again"));
        await agent.WhenIdle();

        var systemEvents = agent.Session.SnapshotEvents()
            .Where(e => e.Type == SessionEventTypes.SystemMessage)
            .ToList();
        Assert.Equal(2, systemEvents.Count);
        Assert.All(systemEvents, sessionEvent => Assert.IsType<SurfaceOp.Append>(sessionEvent.SurfaceOp));
        var messages = agent.Session.DeriveMessages();
        var systems = messages.Where(message => message.Role == MessageRole.System).ToList();
        Assert.Equal(2, systems.Count);
        Assert.DoesNotContain("persona v2", Assert.IsType<TextBlock>(systems[0].Content[0]).Text);
        Assert.Contains("persona v2", Assert.IsType<TextBlock>(systems[^1].Content[0]).Text);
        var systemCount = server.LastRequestBody!.Split("\"role\":\"system\"").Length - 1;
        Assert.Equal(2, systemCount);
    }

    /**
     * 运行期上下文每回合重算(记忆/审批/工作目录), 它变了也不能碰 system prompt:
     * 只追加一条尾部快照, 前缀逐字节不变, 缓存才能跨回合延续。
     */
    [Fact]
    public async Task RuntimeContextChange_DoesNotTouchTheSystemPrompt()
    {
        await using var server = await MockDeepSeekServer.StartAsync(_ => SseTextOnly);
        using var fixture = new HarnessFixture(server.BaseUrl);
        var prompt = fixture.Ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var marker = "alpha";
        using var registration = prompt.Context(new PromptContext("test:runtime", _ => $"Runtime marker: {marker}."));
        var handle = await fixture.Agents.Create(new CreateAgentOptions(
            SessionId.Create("session-runtime-context"),
            null,
            new AgentOptions("test-provider", "test-model")), TestContext.Current.CancellationToken);
        var agent = (AgentLoopAgent)handle.Agent;

        agent.Followup(MessageFactory.CreateUserText("hi"));
        await agent.WhenIdle();
        marker = "bravo";
        agent.Followup(MessageFactory.CreateUserText("again"));
        await agent.WhenIdle();

        var systemEvents = agent.Session.SnapshotEvents()
            .Where(sessionEvent => sessionEvent.Type == SessionEventTypes.SystemMessage)
            .ToList();
        Assert.IsType<SurfaceOp.Append>(Assert.Single(systemEvents).SurfaceOp);
        Assert.Contains("Runtime marker: bravo", server.LastRequestBody);
    }

    [Fact]
    public async Task ReadImage_ToolResultImage_ReachesTheNextRequest()
    {
        var (agent, requests, png) = await RunReadImageTurn(["text", "image"]);

        var toolResult = agent.Session.SnapshotEvents().Select(e => e.Data).OfType<ToolResultPayload>().Single();
        Assert.False(toolResult.Message.Block.IsError);
        using var body = System.Text.Json.JsonDocument.Parse(requests[1]);
        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["tool", "user"], messages[^2..].Select(message => message.GetProperty("role").GetString()));
        Assert.Contains("<type>image</type>", messages[^2].GetProperty("content").GetString());
        var image = messages[^1].GetProperty("content").EnumerateArray().Single(part => part.GetProperty("type").GetString() == "image_url");
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(png)}", image.GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task ReadImage_TextOnlyModel_ReturnsAnErrorInsteadOfAnImage()
    {
        var (agent, requests, _) = await RunReadImageTurn(null);

        var toolResult = agent.Session.SnapshotEvents().Select(e => e.Data).OfType<ToolResultPayload>().Single();
        Assert.True(toolResult.Message.Block.IsError);
        Assert.Contains("does not accept image input", MessageText.Flatten(toolResult.Message.Block.Content));
        Assert.DoesNotContain("image_url", requests[1]);
    }

    [Fact]
    public async Task McpResource_LargeText_IsSpilledWithTheFileThatHoldsItAll()
    {
        var requests = new List<string>();
        await using var server = await MockDeepSeekServer.StartAsync(body =>
        {
            requests.Add(body);
            return requests.Count == 1
                ?
                [
                    "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"read_mcp_resource\",\"arguments\":\"{\\\"server\\\":\\\"res\\\",\\\"uri\\\":\\\"docs://large\\\"}\"}}]}}]}",
                    "",
                    "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}",
                    "",
                    "data: [DONE]",
                    "",
                ]
                : SseTextOnly;
        });
        using var fixture = new HarnessFixture(server.BaseUrl);
        var home = TempTree.CreateDirectory("mcp-spill");
        try
        {
            fixture.Ctx.SetOwn("dshHomePath", home);
            _ = new ToolResultSpill(fixture.Ctx);
            await using var mcp = new Dsh.Mcp.McpService(fixture.Ctx);
            await Mcp.McpResourceServerFixture.ConnectAsync(mcp, Mcp.McpResourceServerFixture.ServerConfig("res"));
            var handle = await fixture.Agents.Create(new CreateAgentOptions(
                SessionId.Create($"session-mcp-spill-{Guid.NewGuid():N}"),
                home,
                new AgentOptions("test-provider", "test-model")), TestContext.Current.CancellationToken);
            var agent = (AgentLoopAgent)handle.Agent;
            agent.Followup(MessageFactory.CreateUserText("read the large resource"));
            await agent.WhenIdle();

            var toolResult = agent.Session.SnapshotEvents().Select(e => e.Data).OfType<ToolResultPayload>().Single();
            Assert.False(toolResult.Message.Block.IsError, MessageText.Flatten(toolResult.Message.Block.Content));
            var text = MessageText.Flatten(toolResult.Message.Block.Content);
            Assert.StartsWith("MCP resource docs://large from server \"res\" (text/plain):\nLARGE-HEAD", text);
            Assert.EndsWith("LARGE-TAIL", text);
            var spilled = System.Text.RegularExpressions.Regex.Match(text, @"chars spilled to (.+?) \.\.\.\]").Groups[1].Value;
            var full = await File.ReadAllTextAsync(spilled, TestContext.Current.CancellationToken);
            Assert.True(text.Length < full.Length / 10, $"kept {text.Length} of {full.Length} chars");
            Assert.EndsWith("LARGE-TAIL", full);
            Assert.Contains(new string('x', 200_000), full);
            Assert.Contains("chars spilled to", requests[1]);
        }
        finally
        {
            TempTree.Delete(home);
        }
    }

    [Fact]
    public async Task ReadImage_HistoryImage_IsOmittedAfterSwitchingToTextOnlyModel()
    {
        var (agent, requests, _) = await RunReadImageTurn(["text", "image"], async (fixture, readAgent, directory) =>
        {
            var commands = CommandsService.Register(fixture.Ctx);
            _ = ModelCommand.Register(fixture.Ctx, HarnessHome.Resolve(Path.Combine(directory, "home")));
            var execution = await commands.Execute(readAgent, $"/model test-provider/{TextOnlyModel}", TestContext.Current.CancellationToken);
            Assert.IsType<CommandResult.Success>(execution!.Result);
            readAgent.Followup(MessageFactory.CreateUserText("describe it again"));
            await readAgent.WhenIdle();
        });

        Assert.Equal(3, requests.Count);
        Assert.IsNotType<TurnEndReason.Error>(agent.Session.SnapshotEvents().Select(e => e.Data).OfType<TurnEndPayload>().Last().Reason);
        var persisted = agent.Session.DeriveMessages()
            .SelectMany(message => message.Content)
            .OfType<ToolResultBlock>()
            .SelectMany(result => result.Content)
            .OfType<ImageBlock>()
            .Single();
        Assert.DoesNotContain("image_url", requests[2]);
        Assert.Contains(TextOnlyImages.OmittedText(persisted.Attachment), requests[2]);
    }

    private static async Task<(AgentLoopAgent Agent, List<string> Requests, byte[] Png)> RunReadImageTurn(
        IReadOnlyList<string>? inputModalities,
        Func<HarnessFixture, AgentLoopAgent, string, Task>? afterReadImage = null)
    {
        var requests = new List<string>();
        await using var server = await MockDeepSeekServer.StartAsync(body =>
        {
            requests.Add(body);
            return requests.Count == 1
                ?
                [
                    "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"read_image\",\"arguments\":\"{\\\"file_path\\\":\\\"shot.png\\\"}\"}}]}}]}",
                    "",
                    "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}",
                    "",
                    "data: [DONE]",
                    "",
                ]
                :
                [
                    """data: {"choices":[{"delta":{"content":"done"}}]}""",
                    "",
                    """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""",
                    "",
                    "data: [DONE]",
                    "",
                ];
        });
        using var fixture = new HarnessFixture(server.BaseUrl, inputModalities: inputModalities);
        var directory = Path.Combine(Path.GetTempPath(), $"dsh-read-image-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using (var image = new Image<Rgba32>(24, 16, new Rgba32(200, 40, 40)))
                await image.SaveAsPngAsync(Path.Combine(directory, "shot.png"), TestContext.Current.CancellationToken);
            _ = new FileAttachmentStore(fixture.Ctx, Path.Combine(directory, "attachments"));
            ReadImageTool.Register(fixture.Ctx);
            var handle = await fixture.Agents.Create(new CreateAgentOptions(
                SessionId.Create($"session-read-image-{Guid.NewGuid():N}"),
                directory,
                new AgentOptions("test-provider", "test-model")), TestContext.Current.CancellationToken);
            var agent = (AgentLoopAgent)handle.Agent;
            agent.Followup(MessageFactory.CreateUserText("look at shot.png"));
            await agent.WhenIdle();
            if (afterReadImage is not null)
                await afterReadImage(fixture, agent, directory);
            return (agent, requests, await File.ReadAllBytesAsync(Path.Combine(directory, "shot.png"), TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
