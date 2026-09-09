using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dsh.Acp;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Sdk;

namespace Dsh.Tests;

public sealed class AcpTests
{
    [Fact]
    public async Task Initialize_ReturnsAcpProtocolMetadata()
    {
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":"1","method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{}}}
            """);
        var output = new StringWriter();
        await using var transport = new JsonRpcLineTransport(input, output);
        var server = new AcpServer(new Cordis.Context(), transport);
        transport.RequestHandler = server.HandleRequestAsync;
        transport.Start();
        await WaitUntilAsync(() => output.ToString().Contains("deepseek-harness-acp"));
        var line = output.ToString().TrimEnd();
        using var document = JsonDocument.Parse(line);
        var result = document.RootElement.GetProperty("result");
        Assert.Equal(1, result.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("deepseek-harness-acp", result.GetProperty("agentInfo").GetProperty("name").GetString());
        Assert.Equal("end_turn", "end_turn");
        await transport.StopAsync();
    }

    [Fact]
    public async Task NewSession_And_CloseSession_Lifecycle()
    {
        var home = Path.Combine(AppContext.BaseDirectory, "acp-test-home", Guid.NewGuid().ToString("N"));
        using (var app = await HarnessComposer.Compose(new HarnessOptions(new HarnessHome(home), Directory.GetCurrentDirectory())))
        {
            var pair = new DuplexTransportPair();
            await using var serverTransport = pair.Server;
            await using var clientTransport = pair.Client;
            var server = new AcpServer(app.Ctx, serverTransport, app.Provider, app.Model);
            serverTransport.RequestHandler = server.HandleRequestAsync;
            serverTransport.Start();
            clientTransport.Start();
            var client = new RawJsonRpcClient(clientTransport);

            var initialize = await client.Request("initialize", new { protocolVersion = 1, clientCapabilities = new { } });
            Assert.Equal("deepseek-harness-acp", initialize.GetProperty("agentInfo").GetProperty("name").GetString());

            var newSession = await client.Request("session/new", new
            {
                cwd = Directory.GetCurrentDirectory(),
                mcpServers = Array.Empty<object>(),
            });
            var sessionId = newSession.GetProperty("sessionId").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(sessionId));
            Assert.NotNull(app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Get(SessionId.Create(sessionId)));

            var close = await client.Request("session/close", new { sessionId });
            Assert.Equal(JsonValueKind.Object, close.ValueKind);
            Assert.Null(app.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Get(SessionId.Create(sessionId)));

            await clientTransport.StopAsync();
            await serverTransport.StopAsync();
        }
        Directory.Delete(home, true);
    }

    [Fact]
    public async Task NewSession_RejectsAdditionalDirectories()
    {
        var home = Path.Combine(AppContext.BaseDirectory, "acp-test-home", Guid.NewGuid().ToString("N"));
        using (var app = await HarnessComposer.Compose(new HarnessOptions(new HarnessHome(home), Directory.GetCurrentDirectory())))
        {
            var pair = new DuplexTransportPair();
            await using var serverTransport = pair.Server;
            await using var clientTransport = pair.Client;
            var server = new AcpServer(app.Ctx, serverTransport, app.Provider, app.Model);
            serverTransport.RequestHandler = server.HandleRequestAsync;
            serverTransport.Start();
            clientTransport.Start();
            var client = new RawJsonRpcClient(clientTransport);

            await Assert.ThrowsAsync<JsonRpcResponseError>(() => client.Request("session/new", new
            {
                cwd = Directory.GetCurrentDirectory(),
                mcpServers = Array.Empty<object>(),
                additionalDirectories = new[] { "C:\\other" },
            }));

            await clientTransport.StopAsync();
            await serverTransport.StopAsync();
        }
        Directory.Delete(home, true);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("condition was not met before the timeout");
            await Task.Delay(10);
        }
    }

    private sealed class RawJsonRpcClient(JsonRpcLineTransport transport)
    {
        public async Task<JsonElement> Request(string method, object? parameters)
        {
            var result = await transport.RequestAsync(method, parameters);
            return (JsonElement)result!;
        }
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
