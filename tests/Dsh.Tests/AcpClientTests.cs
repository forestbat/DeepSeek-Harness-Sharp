using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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
        await using var server = await MockDeepSeekServer.StartAsync(_ => SseTextOnly);
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

        var initialize = await client.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(AcpMethods.ProtocolVersion, initialize.ProtocolVersion);
        Assert.Equal("deepseek-harness-acp", initialize.AgentInfo?.Name);

        var sessionId = (await client.NewSessionAsync(Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken)).SessionId!;
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
        await using var server = await MockDeepSeekServer.StartAsync(_ => ++scriptCalls == 1 ? SseToolCall : SseDone);
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

        await client.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        var sessionId = (await client.NewSessionAsync(Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken)).SessionId!;
        var stopReason = await client.PromptAsync(sessionId, "echo please", TestContext.Current.CancellationToken);

        Assert.Equal("end_turn", stopReason);
        Assert.Equal(1, echoCalls);
        Assert.Equal(2, scriptCalls);
        Assert.Contains(updates, update => update.Kind == AcpMethods.UpdateToolCall);
        Assert.Contains(updates, update => update.Kind == AcpMethods.UpdateAgentMessageChunk && update.Text == "done");
        await clientTransport.StopAsync();
        await serverTransport.StopAsync();
    }

    [Fact]
    public async Task SocketServer_RequiresLocalTokenBeforeSessions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await MockDeepSeekServer.StartAsync(_ => SseTextOnly);
        using var fixture = new HarnessFixture(server.BaseUrl);
        var root = TempTree.CreateSocketDirectory("a");
        try
        {
            using var listener = new AcpSocketServer(fixture.Ctx, new Dsh.Boot.HarnessHome(root), "test-provider", "test-model");
            listener.Start();
            using var socket = await ConnectAsync(listener.Endpoint, cancellationToken);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await using var transport = new JsonRpcLineTransport(new StreamReader(stream), new StreamWriter(stream) { AutoFlush = true });
            transport.Start();
            var client = new AcpClient(transport);
            var cwd = Directory.GetCurrentDirectory();

            var initialize = await client.InitializeAsync(cancellationToken: cancellationToken);
            Assert.Equal(AcpMethods.LocalTokenAuthMethod, Assert.Single(initialize.AuthMethods!).Id);
            await Assert.ThrowsAsync<JsonRpcResponseError>(() => client.NewSessionAsync(cwd, cancellationToken));
            await Assert.ThrowsAsync<JsonRpcResponseError>(() => client.AuthenticateAsync("not-the-token", cancellationToken));
            await Assert.ThrowsAsync<JsonRpcResponseError>(() => client.NewSessionAsync(cwd, cancellationToken));

            var token = Dsh.Transport.LocalEndpointToken.Read(Path.Combine(root, "run", "dsh-acp.sock"));
            await client.AuthenticateAsync(token!, cancellationToken);
            var sessionId = (await client.NewSessionAsync(cwd, cancellationToken)).SessionId!;

            Assert.False(string.IsNullOrWhiteSpace(sessionId));
            await client.CloseSessionAsync(sessionId, cancellationToken);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Timeout = 5000)]
    public async Task SocketServer_OversizedLine_GetsErrorAndDisconnects_AndKeepsServing()
    {
        const int oversizedLineChars = 2 * 1024 * 1024;
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("o");
        try
        {
            using var listener = new AcpSocketServer(new Context(), new Dsh.Boot.HarnessHome(root));
            listener.Start();
            using (var socket = await ConnectAsync(listener.Endpoint, cancellationToken))
            {
                await using var stream = new NetworkStream(socket, ownsSocket: false);
                try
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes($"{new string('x', oversizedLineChars)}\n"), cancellationToken);
                }
                catch (IOException)
                {
                    // The server may drop the connection before the client finished writing the line.
                }
                var reply = await ReadUntilDisconnectedAsync(stream, cancellationToken);
                Assert.True(reply.Length == 0 || reply.Contains("exceeds"), reply);
            }

            using var next = await ConnectAsync(listener.Endpoint, cancellationToken);
            await using var nextStream = new NetworkStream(next, ownsSocket: false);
            await using var transport = new JsonRpcLineTransport(new StreamReader(nextStream), new StreamWriter(nextStream) { AutoFlush = true });
            transport.Start();
            var initialize = await new AcpClient(transport).InitializeAsync(cancellationToken: cancellationToken);
            Assert.Equal(AcpMethods.ProtocolVersion, initialize.ProtocolVersion);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public void SocketServer_RestrictsSocketAndRunDirectoryToOwner()
    {
        if (OperatingSystem.IsWindows())
            return;
        var root = TempTree.CreateSocketDirectory("m");
        try
        {
            using var listener = new AcpSocketServer(new Context(), new Dsh.Boot.HarnessHome(root));
            listener.Start();
            var runDirectory = Path.Combine(root, "run");

            TempTree.AssertFitsMacOsSocketLimit(Path.Combine(runDirectory, "dsh-acp.sock"));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(runDirectory));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(runDirectory, "dsh-acp.sock")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(runDirectory, "dsh-acp.token")));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    /**
     * Returns everything the server sent until it dropped the connection.
     * The server stops reading at the limit, so closing with the rest of the line unread makes the kernel reset the connection, which may discard the error reply.
     */
    private static async Task<string> ReadUntilDisconnectedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        try
        {
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static async Task<Socket> ConnectAsync(string endpoint, CancellationToken cancellationToken)
    {
        var uri = new Uri(endpoint);
        if (uri.Scheme == "unix")
        {
            var socketPath = endpoint["unix://".Length..];
            TempTree.AssertFitsMacOsSocketLimit(socketPath);
            var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await unix.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
            return unix;
        }
        var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await tcp.ConnectAsync(IPAddress.Loopback, uri.Port, cancellationToken);
        return tcp;
    }

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

        private async Task Serve(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            context.Response.ContentType = "text/event-stream";
            foreach (var line in _script(body))
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
