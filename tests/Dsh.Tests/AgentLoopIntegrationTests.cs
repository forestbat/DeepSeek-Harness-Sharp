using System.Net;
using System.Text;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tests;

public class AgentLoopIntegrationTests
{
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

    private sealed class MockDeepSeekServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<string, IReadOnlyList<string>> _script;

        public MockDeepSeekServer(Func<string, IReadOnlyList<string>> script)
        {
            _script = script;
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

        public string? LastRequestBody { get; private set; }

        private async Task Serve()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                using var reader = new StreamReader(context.Request.InputStream);
                LastRequestBody = await reader.ReadToEndAsync();
                context.Response.ContentType = "text/event-stream";
                foreach (var line in _script(LastRequestBody))
                {
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    await context.Response.OutputStream.WriteAsync(bytes);
                    await context.Response.OutputStream.FlushAsync();
                }
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    private sealed class HarnessFixture : IDisposable
    {
        public Context Ctx { get; }
        public SessionStore Sessions { get; }
        public ToolRuntime Tools { get; }
        public AgentRegistry Agents { get; }
        public AgentLoop Loop { get; }

        public HarnessFixture(string baseUrl)
        {
            Ctx = new Context();
            Sessions = new SessionStore(Ctx);
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            var llm = new LlmRuntime(Ctx);
            Agents = new AgentRegistry(Ctx);
            Loop = new AgentLoop(Ctx);
            var connection = new Dsh.Llm.DeepSeek.DeepSeekConnectionOptions(
                baseUrl,
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
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task TextTurn_LogsFullEventSequence()
    {
        using var server = new MockDeepSeekServer(_ => SseTextOnly);
        using var fixture = new HarnessFixture(server.BaseUrl);
        var handle = await fixture.Agents.Create(new CreateAgentOptions(
            SessionId.Create("session-test-1"),
            null,
            new AgentOptions("test-provider", "test-model")));
        var agent = (AgentLoopAgent)handle.Agent;

        agent.Followup(MessageFactory.CreateUserText("hi"));
        await agent.WhenIdle();

        var types = agent.Session.SnapshotEvents().Select(e => e.Type).Where(t => t != SessionEventTypes.AgentInboxSpliced).ToList();
        Assert.Equal(
            [
                SessionEventTypes.TurnStart,
                SessionEventTypes.StepStart,
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
        Assert.Equal(2, messages.Count);
        Assert.Contains("test-model", server.LastRequestBody);
        Assert.Contains("\"stream\":true", server.LastRequestBody);
    }

    [Fact]
    public async Task ToolCallTurn_ExecutesAndLoops()
    {
        var calls = 0;
        var scriptCalls = 0;
        using var server = new MockDeepSeekServer(_ =>
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
            new AgentOptions("test-provider", "test-model")));
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
}
