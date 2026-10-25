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
    /** 零配置不兜底后,SDK 测试需要显式 provider 配置;baseUrl 指向回环端口避免真实网络请求。 */
    private static void WriteProviderSettings(string home)
    {
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "settings.yaml"), """
            global_default_model: deepseek-official/deepseek-v4-flash
            providers:
              deepseek-official:
                type: openai-compatible
                options:
                  baseUrl: http://127.0.0.1:9
                  apiKey: sk-test
                models:
                  deepseek-v4-flash: {}
            """);
    }

    [Fact(Timeout = 5000)]
    public async Task Transport_HandlesIncomingRequestAndWritesResponse()
    {
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":"1","method":"echo","params":{"value":"ok"}}
            """);
        var output = new JsonRpcLineCapture();
        await using var transport = new JsonRpcLineTransport(input, output);
        transport.RequestHandler = (method, parameters) =>
        {
            Assert.Equal("echo", method);
            var value = parameters!.Value.GetProperty("value").GetString();
            return Task.FromResult<object?>(new Dictionary<string, object?> { ["echo"] = value });
        };
        transport.Start();
        using var document = await output.NextMessageAsync(TestContext.Current.CancellationToken);
        Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal("1", document.RootElement.GetProperty("id").GetString());
        Assert.Equal("ok", document.RootElement.GetProperty("result").GetProperty("echo").GetString());
        await transport.StopAsync();
    }

    [Fact(Timeout = 5000)]
    public async Task Transport_OversizedLine_GetsErrorAndClosesWithoutReadingFurther()
    {
        const int oversizedLineChars = 2 * 1024 * 1024;
        var input = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(
            $"{EchoRequest("1")}\r\n{EchoRequest("2")}\n{new string('x', oversizedLineChars)}\n{EchoRequest("3")}\n")));
        var output = new StringWriter();
        var handled = new List<string>();
        await using var transport = new JsonRpcLineTransport(input, output);
        transport.RequestHandler = (_, parameters) =>
        {
            handled.Add(parameters!.Value.GetProperty("value").GetString()!);
            return Task.FromResult<object?>(null);
        };

        transport.Start();
        await transport.WhenClosedAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(["1", "2"], handled);
        using var error = JsonDocument.Parse(output.ToString().TrimEnd().Split('\n')[^1]);
        Assert.Equal(JsonValueKind.Null, error.RootElement.GetProperty("id").ValueKind);
        Assert.Contains("exceeds", error.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    private static string EchoRequest(string value)
        => $$$"""{"jsonrpc":"2.0","id":"{{{value}}}","method":"echo","params":{"value":"{{{value}}}"}}""";

    [Fact]
    public async Task Transport_PreCancelledRequest_WritesNoFrame()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var output = new StringWriter();
        await using var transport = new JsonRpcLineTransport(new StringReader(""), output);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.RequestAsync("cancelled", cancellationToken: cancellation.Token));

        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task Transport_StopBeforeStart_FailsPendingCallsAndCompletesClosed()
    {
        await using var transport = new JsonRpcLineTransport(new StringReader(""), new StringWriter());
        var pending = transport.RequestAsync("pending", cancellationToken: TestContext.Current.CancellationToken);

        await transport.StopAsync();

        await Assert.ThrowsAsync<JsonRpcTransportClosedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Null(await transport.Closed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<JsonRpcTransportClosedException>(() => transport.RequestAsync("after-close", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Transport_OutputFailure_FailsAllPendingCallsAndPreventsFurtherFrames()
    {
        var output = new FailingFlushWriter();
        await using var transport = new JsonRpcLineTransport(new StringReader(""), output);
        var first = transport.RequestAsync("first", cancellationToken: TestContext.Current.CancellationToken);
        output.FailFlush = true;

        var second = transport.RequestAsync("second", cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(() => first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.IsType<IOException>(await transport.Closed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var written = output.ToString();
        Assert.Throws<IOException>(() => transport.Notify("after-failure"));
        Assert.Equal(written, output.ToString());
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

        var result = await clientTransport.RequestAsync("initialize", new { cwd = "C:\\work", provider = "p", model = "m" }, TestContext.Current.CancellationToken);
        using var resultDocument = JsonDocument.Parse(((JsonElement)result!).GetRawText());
        Assert.True(resultDocument.RootElement.GetProperty("ok").GetBoolean());

        serverTransport.Notify("session.event", new { sessionId = "s1" });
        Assert.Equal("s1", await received.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        var requestLines = pair.ClientWriter.WrittenLines;
        Assert.Contains(requestLines, line => line.Contains("initialize"));

        await clientTransport.StopAsync();
        await serverTransport.StopAsync();
    }

    [Fact]
    public async Task SdkServer_InitializeAndPromptThroughCSharpClient()
    {
        var home = Path.Combine(AppContext.BaseDirectory, "sdk-test-home", Guid.NewGuid().ToString("N"));
        WriteProviderSettings(home);
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
    public async Task SdkServer_ServiceGateway_CallsServices()
    {
        var home = Path.Combine(AppContext.BaseDirectory, "sdk-test-home", Guid.NewGuid().ToString("N"));
        WriteProviderSettings(home);
        using (var app = await HarnessComposer.Compose(new HarnessOptions(new HarnessHome(home), Directory.GetCurrentDirectory())))
        {
            var pair = new DuplexTransportPair();
            await using var serverTransport = pair.Server;
            var server = new HarnessSdkServer(app.Ctx, serverTransport);

            var providersResult = await server.HandleRequestAsync(SdkMethods.ServiceCall,
                JsonSerializer.SerializeToElement(new ServiceCallParams("llm", "ListProviders"), DshJson.Options));
            var providers = (JsonElement)providersResult!;
            Assert.Contains(providers.EnumerateArray(), entry => entry.GetProperty("id").GetString() == "deepseek-official");

        }
        Directory.Delete(home, true);
    }

    [Fact]
    public async Task SdkServer_EmitsSessionEventNotificationToCSharpClient()
    {
        var home = Path.Combine(AppContext.BaseDirectory, "sdk-test-home", Guid.NewGuid().ToString("N"));
        WriteProviderSettings(home);
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

            var notification = await subscription.NextAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
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

    [Fact(Timeout = 5000)]
    public async Task SdkServer_AcceptsTsWireInitializeRequest()
    {
        var home = Path.Combine(AppContext.BaseDirectory, "sdk-test-home", Guid.NewGuid().ToString("N"));
        WriteProviderSettings(home);
        using (var app = await HarnessComposer.Compose(new HarnessOptions(new HarnessHome(home), Directory.GetCurrentDirectory())))
        {
            var input = new StringReader("""
                {"jsonrpc":"2.0","id":"1","method":"initialize","params":{"cwd":"C:\\work","provider":"deepseek-official","model":"deepseek-v4-flash"}}
                """);
            var output = new JsonRpcLineCapture();
            await using var transport = new JsonRpcLineTransport(input, output);
            var server = new HarnessSdkServer(app.Ctx, transport);
            transport.RequestHandler = server.HandleRequestAsync;
            transport.Start();
            using var document = await output.NextMessageAsync(TestContext.Current.CancellationToken);
            Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
            Assert.Equal("1", document.RootElement.GetProperty("id").GetString());
            Assert.Equal("deepseek-harness-sdk-runtime",
                document.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
            await transport.StopAsync();
        }
        Directory.Delete(home, true);
    }

    [Fact]
    public async Task SdkServer_ServiceCall_AwaitsGenericValueTask()
    {
        var (server, transport, _) = ProbeServer();
        await using var lifetime = transport;

        var result = await server.ServiceCallAsync(new ServiceCallParams(SdkProbeService.Name, nameof(SdkProbeService.CountAsync)));

        Assert.Equal(SdkProbeService.Count, ((JsonElement)result!).GetInt32());
    }

    [Fact]
    public async Task SdkServer_ServiceCall_RejectsMethodsNotDeclaredRemoteCallable()
    {
        var (server, transport, probe) = ProbeServer();
        await using var lifetime = transport;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            server.ServiceCallAsync(new ServiceCallParams(SdkProbeService.Name, nameof(SdkProbeService.Reset))));

        Assert.False(probe.ResetCalled);
    }

    [Fact]
    public async Task SdkServer_ServiceCall_AllowsMethodsOfRemoteCallableInterface()
    {
        var (server, transport, _) = ProbeServer();
        await using var lifetime = transport;

        var result = await server.ServiceCallAsync(new ServiceCallParams(SdkProbeService.Name, nameof(ISdkProbeQueries.Describe)));

        Assert.Equal(SdkProbeService.Description, ((JsonElement)result!).GetString());
    }

    [Fact]
    public async Task SdkServer_ServiceCall_RejectsServiceOfInactiveOwner()
    {
        var ctx = new Dsh.Runtime.Context();
        var probe = new SdkProbeService();
        var activation = ctx.Plugin(Dsh.Runtime.PluginDefinition.From((pluginCtx, _) =>
        {
            pluginCtx.Provide(SdkProbeService.Name, probe);
            throw new InvalidOperationException("activation failed");
        }, "failing-probe"));
        await activation.WaitAsync();
        var pair = new DuplexTransportPair();
        await using var transport = pair.Server;
        var server = new HarnessSdkServer(ctx, transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            server.ServiceCallAsync(new ServiceCallParams(SdkProbeService.Name, nameof(ISdkProbeQueries.Describe))));
    }

    private static (HarnessSdkServer Server, JsonRpcLineTransport Transport, SdkProbeService Probe) ProbeServer()
    {
        var ctx = new Dsh.Runtime.Context();
        var probe = new SdkProbeService();
        ctx.Provide(SdkProbeService.Name, probe);
        var pair = new DuplexTransportPair();
        return (new HarnessSdkServer(ctx, pair.Server), pair.Server, probe);
    }

    [Dsh.Runtime.RemoteCallable]
    public interface ISdkProbeQueries
    {
        string Describe();
    }

    /** Test fixture service: exposes one whitelisted ValueTask<T> method, one whitelisted interface and one method without the marker. */
    public sealed class SdkProbeService : ISdkProbeQueries
    {
        public const string Name = "sdk-probe";
        public const int Count = 42;
        public const string Description = "sdk probe";

        public bool ResetCalled { get; private set; }

        [Dsh.Runtime.RemoteCallable]
        public ValueTask<int> CountAsync() => ValueTask.FromResult(Count);

        public string Describe() => Description;

        public void Reset() => ResetCalled = true;
    }

    private sealed class FailingFlushWriter : StringWriter
    {
        public bool FailFlush { get; set; }

        public override void Flush()
        {
            if (FailFlush)
                throw new IOException("output closed while flushing a frame");
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

    private sealed class ChannelTextWriter(Channel<string> channel) : TextWriter
    {
        private readonly List<string> _writtenLines = [];
        private readonly Lock _sync = new();

        public IReadOnlyList<string> WrittenLines
        {
            get
            {
                lock (_sync)
                    return [.. _writtenLines];
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
