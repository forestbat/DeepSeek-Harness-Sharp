using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Sdk;

namespace Dsh.Tests;

public sealed class SdkTests
{
    [Fact]
    public async Task Transport_HandlesIncomingRequestAndWritesResponse()
    {
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":"1","method":"echo","params":{"value":"ok"}}
            """);
        var output = new StringWriter();
        await using var transport = new JsonRpcLineTransport(input, output);
        transport.RequestHandler = (method, parameters) =>
        {
            Assert.Equal("echo", method);
            var value = parameters!.Value.GetProperty("value").GetString();
            return Task.FromResult<object?>(new Dictionary<string, object?> { ["echo"] = value });
        };
        transport.Start();
        await WaitUntilAsync(() => output.ToString().Contains("\"echo\""));
        var line = output.ToString().TrimEnd();
        using var document = JsonDocument.Parse(line);
        Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal("1", document.RootElement.GetProperty("id").GetString());
        Assert.Equal("ok", document.RootElement.GetProperty("result").GetProperty("echo").GetString());
        await transport.StopAsync();
    }

    [Fact]
    public async Task Transport_ClientRequestAndNotificationFlow()
    {
        var pair = new DuplexTransportPair();
        await using var serverTransport = pair.Server;
        await using var clientTransport = pair.Client;
        serverTransport.RequestHandler = (method, _) =>
        {
            Assert.Equal("initialize", method);
            return Task.FromResult<object?>(new Dictionary<string, object?> { ["ok"] = true });
        };
        serverTransport.Start();
        clientTransport.Start();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientTransport.NotificationHandler = (method, parameters) =>
        {
            if (method == "session.event")
                received.TrySetResult(parameters!.Value.GetProperty("sessionId").GetString()!);
        };

        var result = await clientTransport.RequestAsync("initialize", new { cwd = "C:\\work", provider = "p", model = "m" });
        using var resultDocument = JsonDocument.Parse(((JsonElement)result!).GetRawText());
        Assert.True(resultDocument.RootElement.GetProperty("ok").GetBoolean());

        serverTransport.Notify("session.event", new { sessionId = "s1" });
        Assert.Equal("s1", await received.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        var requestLines = pair.ClientWriter.WrittenLines;
        Assert.Contains(requestLines, line => line.Contains("initialize"));

        await clientTransport.StopAsync();
        await serverTransport.StopAsync();
    }

    [Fact]
    public async Task SdkServer_InitializeAndPromptThroughCSharpClient()
    {
        var home = Path.Combine(AppContext.BaseDirectory, "sdk-test-home", Guid.NewGuid().ToString("N"));
        using (var app = await HarnessComposer.Compose(new HarnessOptions(new HarnessHome(home), Directory.GetCurrentDirectory())))
        {
            var pair = new DuplexTransportPair();
            await using var serverTransport = pair.Server;
            await using var clientTransport = pair.Client;
            var server = new HarnessSdkServer(app.Ctx, serverTransport);
            serverTransport.RequestHandler = server.HandleRequestAsync;
            serverTransport.Start();
            clientTransport.Start();
            await using var client = new HarnessClient(clientTransport);

            var initialize = await client.InitializeAsync(new InitializeParams(
                Directory.GetCurrentDirectory(),
                "deepseek-official",
                "deepseek-v4-flash"));
            Assert.Equal("deepseek-harness-sdk-runtime", initialize.ServerInfo.Name);

            var messageId = await client.PromptAsync("session-1", [new TextBlock("hello")]);
            Assert.False(string.IsNullOrWhiteSpace(messageId));

            var sessions = app.Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
            var session = sessions.Get(SessionId.Create("session-1"));
            Assert.NotNull(session);

            await clientTransport.StopAsync();
            await serverTransport.StopAsync();
        }
        Directory.Delete(home, true);
    }

    [Fact]
    public async Task SdkServer_EmitsSessionEventNotificationToCSharpClient()
    {
        var home = Path.Combine(AppContext.BaseDirectory, "sdk-test-home", Guid.NewGuid().ToString("N"));
        using (var app = await HarnessComposer.Compose(new HarnessOptions(new HarnessHome(home), Directory.GetCurrentDirectory())))
        {
            var pair = new DuplexTransportPair();
            await using var serverTransport = pair.Server;
            await using var clientTransport = pair.Client;
            var server = new HarnessSdkServer(app.Ctx, serverTransport);
            serverTransport.RequestHandler = server.HandleRequestAsync;
            serverTransport.Start();
            clientTransport.Start();
            await using var client = new HarnessClient(clientTransport);
            var subscription = client.Subscribe((method, _) => method == SdkMethods.SessionEvent);

            await client.InitializeAsync(new InitializeParams(
                Directory.GetCurrentDirectory(),
                "deepseek-official",
                "deepseek-v4-flash"));
            await client.PromptAsync("session-event-test", [new TextBlock("hello")]);

            var notification = await subscription.NextAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(SdkMethods.SessionEvent, notification.Method);
            var parameters = notification.Parameters!.Value;
            Assert.Equal("session-event-test", parameters.GetProperty("sessionId").GetString());
            Assert.True(parameters.GetProperty("event").ValueKind == JsonValueKind.Object);

            subscription.Dispose();
            await clientTransport.StopAsync();
            await serverTransport.StopAsync();
        }
        Directory.Delete(home, true);
    }

    [Fact]
    public async Task SdkServer_AcceptsTsWireInitializeRequest()
    {
        var home = Path.Combine(AppContext.BaseDirectory, "sdk-test-home", Guid.NewGuid().ToString("N"));
        using (var app = await HarnessComposer.Compose(new HarnessOptions(new HarnessHome(home), Directory.GetCurrentDirectory())))
        {
            var input = new StringReader("""
                {"jsonrpc":"2.0","id":"1","method":"initialize","params":{"cwd":"C:\\work","provider":"deepseek-official","model":"deepseek-v4-flash"}}
                """);
            var output = new StringWriter();
            await using var transport = new JsonRpcLineTransport(input, output);
            var server = new HarnessSdkServer(app.Ctx, transport);
            transport.RequestHandler = server.HandleRequestAsync;
            transport.Start();
            await WaitUntilAsync(() => output.ToString().Contains("serverInfo"));
            var line = output.ToString().TrimEnd();
            using var document = JsonDocument.Parse(line);
            Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
            Assert.Equal("1", document.RootElement.GetProperty("id").GetString());
            Assert.Equal("deepseek-harness-sdk-runtime",
                document.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
            await transport.StopAsync();
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

    private sealed class DuplexTransportPair
    {
        private readonly Channel<string> _serverToClient = Channel.CreateUnbounded<string>();
        private readonly Channel<string> _clientToServer = Channel.CreateUnbounded<string>();

        public JsonRpcLineTransport Server { get; }
        public JsonRpcLineTransport Client { get; }
        public ChannelTextWriter ClientWriter { get; }
        private ChannelTextWriter ServerWriter { get; }

        public DuplexTransportPair()
        {
            ServerWriter = new ChannelTextWriter(_serverToClient);
            ClientWriter = new ChannelTextWriter(_clientToServer);
            Server = new JsonRpcLineTransport(
                new ChannelTextReader(_clientToServer),
                ServerWriter);
            Client = new JsonRpcLineTransport(
                new ChannelTextReader(_serverToClient),
                ClientWriter);
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
        private readonly List<string> _writtenLines = [];
        private readonly Lock _sync = new();

        public IReadOnlyList<string> WrittenLines
        {
            get
            {
                lock (_sync)
                    return [.._writtenLines];
            }
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lock (_sync)
                _writtenLines.Add(value ?? "");
            channel.Writer.TryWrite(value ?? "");
        }

        public override void Flush()
        {
        }
    }
}
