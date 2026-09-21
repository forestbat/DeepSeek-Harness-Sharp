using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Acp;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Sdk;

namespace Dsh.Tests;

public sealed class AcpClientTests
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

    private static readonly string[] SseToolCall =
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

    private static readonly string[] SseDone =
    [
        """data: {"choices":[{"delta":{"content":"done"}}]}""",
        "",
        """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""",
        "",
        "data: [DONE]",
        "",
    ];

    [Fact]
    public async Task Client_CompletesProtocolRoundTrip()
    {
        using var server = new MockDeepSeekServer(_ => SseTextOnly);
        using var fixture = new HarnessFixture(server.BaseUrl);
        var pair = new DuplexTransportPair();
        await using var serverTransport = pair.Server;
        await using var clientTransport = pair.Client;
        var acpServer = new AcpServer(fixture.Ctx, serverTransport, "test-provider", "test-model");
        serverTransport.RequestHandler = acpServer.HandleRequestAsync;
        serverTransport.Start();
        clientTransport.Start();
        var client = new AcpClient(clientTransport);
        var updates = new List<AcpSessionUpdate>();
        client.SessionUpdate += updates.Add;

        var initialize = await client.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AcpMethods.ProtocolVersion, initialize.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("deepseek-harness-acp", initialize.GetProperty("agentInfo").GetProperty("name").GetString());

        var sessionId = await client.NewSessionAsync(Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        var stopReason = await client.PromptAsync(sessionId, "hi", TestContext.Current.CancellationToken);
        Assert.Equal("end_turn", stopReason);

        var chunks = updates.Where(update => update.Kind == AcpMethods.UpdateAgentMessageChunk).ToList();
        Assert.Equal("Hello world", string.Concat(chunks.Select(update => update.Text)));
        Assert.All(updates, update => Assert.Equal(sessionId, update.SessionId));

        await client.CloseSessionAsync(sessionId, TestContext.Current.CancellationToken);
        Assert.Null(fixture.Agents.Get(SessionId.Create(sessionId)));
        await clientTransport.StopAsync();
        await serverTransport.StopAsync();
    }

    [Fact]
    public async Task Client_AcpPathAutoApprovesToolCalls()
    {
        var scriptCalls = 0;
        using var server = new MockDeepSeekServer(_ => ++scriptCalls == 1 ? SseToolCall : SseDone);
        using var fixture = new HarnessFixture(server.BaseUrl);
        _ = ApprovalService.Register(fixture.Ctx);
        var echoCalls = 0;
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
                echoCalls++;
                return Task.FromResult<object?>(new { echo = args.GetProperty("text").GetString()! });
            },
            IsConcurrencySafe = _ => true,
        });
        fixture.Ctx.OnWaterfall<ToolPreExecuteNotification>((_, _) =>
            ValueTask.FromResult<object?>(new PreToolDecision.Ask()), new EventOptions { Global = true });

        var pair = new DuplexTransportPair();
        await using var serverTransport = pair.Server;
        await using var clientTransport = pair.Client;
        var acpServer = new AcpServer(fixture.Ctx, serverTransport, "test-provider", "test-model",
            ApprovalAnswerers.AutoApproveScoped);
        serverTransport.RequestHandler = acpServer.HandleRequestAsync;
        serverTransport.Start();
        clientTransport.Start();
        var client = new AcpClient(clientTransport);
        var updates = new List<AcpSessionUpdate>();
        client.SessionUpdate += updates.Add;

        await client.InitializeAsync(TestContext.Current.CancellationToken);
        var sessionId = await client.NewSessionAsync(Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken);
        var stopReason = await client.PromptAsync(sessionId, "echo please", TestContext.Current.CancellationToken);

        Assert.Equal("end_turn", stopReason);
        Assert.Equal(1, echoCalls);
        Assert.Equal(2, scriptCalls);
        Assert.Contains(updates, update => update.Kind == AcpMethods.UpdateToolCall);
        Assert.Contains(updates, update => update.Kind == AcpMethods.UpdateAgentMessageChunk && update.Text == "done");
        await clientTransport.StopAsync();
        await serverTransport.StopAsync();
    }

    private sealed class MockDeepSeekServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<string, IReadOnlyList<string>> _script;

        public MockDeepSeekServer(Func<string, IReadOnlyList<string>> script)
        {
            _script = script;
            var probe = new TcpListener(IPAddress.Loopback, 0);
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
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                using var streamReader = new StreamReader(context.Request.InputStream);
                var body = await streamReader.ReadToEndAsync();
                context.Response.ContentType = "text/event-stream";
                foreach (var line in _script(body))
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

    private sealed class DuplexTransportPair
    {
        private readonly Channel<string> _serverToClient = Channel.CreateUnbounded<string>();
        private readonly Channel<string> _clientToServer = Channel.CreateUnbounded<string>();

        public JsonRpcLineTransport Server { get; }
        public JsonRpcLineTransport Client { get; }

        public DuplexTransportPair()
        {
            Server = new JsonRpcLineTransport(
                new ChannelTextReader(_clientToServer),
                new ChannelTextWriter(_serverToClient));
            Client = new JsonRpcLineTransport(
                new ChannelTextReader(_serverToClient),
                new ChannelTextWriter(_clientToServer));
        }
    }

    private sealed class ChannelTextReader(Channel<string> channel) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public override string? ReadLine()
        {
            return channel.Reader.TryRead(out var line) ? line : null;
        }
    }

    private sealed class ChannelTextWriter(Channel<string> channel) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            channel.Writer.TryWrite(value ?? "");
        }

        public override void Flush()
        {
        }
    }
}
