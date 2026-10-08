using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dsh.Transport;

namespace Dsh.Tests;

public sealed class FrameCodecTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RoundTripsPayload()
    {
        using var stream = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes("hello 世界");
        await FrameCodec.WriteFrameAsync(stream, payload, Ct);
        stream.Position = 0;
        Assert.Equal(payload, await FrameCodec.ReadFrameAsync(stream, cancellationToken: Ct));
    }

    [Fact]
    public async Task ReadsFramesBackToBack()
    {
        var first = new byte[] { 1, 2, 3 };
        var second = new byte[] { 4, 5 };
        using var stream = new MemoryStream([.. BuildFrame(first), .. BuildFrame(second)]);
        Assert.Equal(first, await FrameCodec.ReadFrameAsync(stream, cancellationToken: Ct));
        Assert.Equal(second, await FrameCodec.ReadFrameAsync(stream, cancellationToken: Ct));
        Assert.Null(await FrameCodec.ReadFrameAsync(stream, cancellationToken: Ct));
    }

    [Fact]
    public async Task ReadsAcrossPartialChunks()
    {
        var payload = new byte[] { 10, 20, 30, 40, 50, 60, 70 };
        using var stream = new DripStream(BuildFrame(payload));
        Assert.Equal(payload, await FrameCodec.ReadFrameAsync(stream, cancellationToken: Ct));
    }

    [Fact]
    public async Task ReturnsNullAtCleanEof()
    {
        using var stream = new MemoryStream();
        Assert.Null(await FrameCodec.ReadFrameAsync(stream, cancellationToken: Ct));
    }

    [Fact]
    public async Task RejectsOversizeLength()
    {
        var header = new byte[FrameCodec.HeaderLength];
        BinaryPrimitives.WriteInt32BigEndian(header, 4096);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<IOException>(
            async () => await FrameCodec.ReadFrameAsync(stream, maxFrameBytes: 16, cancellationToken: Ct));
    }

    private static byte[] BuildFrame(byte[] payload)
    {
        var framed = new byte[FrameCodec.HeaderLength + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(framed, payload.Length);
        payload.CopyTo(framed, FrameCodec.HeaderLength);
        return framed;
    }

    /** 每次只吐 1 字节, 逼出拆包路径。 */
    private sealed class DripStream(byte[] data) : Stream
    {
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset >= data.Length)
                return 0;
            buffer[offset] = data[_offset++];
            return 1;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class JsonRpcPeerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RequestReceivesHandlerResult()
    {
        await using var fixture = await DuplexFixture.CreateAsync(Ct);
        fixture.Server.RequestHandler = (_, _) =>
            ValueTask.FromResult<JsonElement?>(Parse("""{"ok":true,"echo":"pong"}"""));
        var result = await fixture.Client.RequestAsync("ping", Parse("""{"n":1}"""), Ct);
        Assert.True(result!.Value.GetProperty("ok").GetBoolean());
        Assert.Equal("pong", result.Value.GetProperty("echo").GetString());
    }

    [Fact]
    public async Task MissingHandlerReturnsMethodNotFound()
    {
        await using var fixture = await DuplexFixture.CreateAsync(Ct);
        var error = await Assert.ThrowsAsync<JsonRpcException>(
            async () => await fixture.Client.RequestAsync("nope", cancellationToken: Ct));
        Assert.Equal(JsonRpcPeer.MethodNotFound, error.Code);
    }

    [Fact]
    public async Task HandlerExceptionBecomesJsonRpcError()
    {
        await using var fixture = await DuplexFixture.CreateAsync(Ct);
        fixture.Server.RequestHandler = (_, _) =>
            throw new JsonRpcException(-32000, "boom");
        var error = await Assert.ThrowsAsync<JsonRpcException>(
            async () => await fixture.Client.RequestAsync("fail", cancellationToken: Ct));
        Assert.Equal(-32000, error.Code);
        Assert.Equal("boom", error.Message);
    }

    [Fact]
    public async Task NotificationIsDeliveredWithoutReply()
    {
        await using var fixture = await DuplexFixture.CreateAsync(Ct);
        var received = new TaskCompletionSource<JsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.NotificationReceived += message => received.TrySetResult(message);
        await fixture.Client.NotifyAsync("event", Parse("""{"seq":7}"""), Ct);
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("event", message.Method);
        Assert.Equal(7, message.Params!.Value.GetProperty("seq").GetInt32());
    }

    [Fact]
    public async Task PendingRequestFailsWhenPeerCloses()
    {
        await using var fixture = await DuplexFixture.CreateAsync(Ct);
        fixture.Server.RequestHandler = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return null;
        };
        var pending = fixture.Client.RequestAsync("hang", cancellationToken: Ct);
        await fixture.Server.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(async () => await pending);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /** 一对经 TCP loopback 相连的 peer, 保证真实双工流(非同进程回调)。 */
    private sealed class DuplexFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _client;
        private readonly TcpClient _server;

        private DuplexFixture(TcpListener listener, TcpClient client, TcpClient server, JsonRpcPeer clientPeer, JsonRpcPeer serverPeer)
        {
            _listener = listener;
            _client = client;
            _server = server;
            Client = clientPeer;
            Server = serverPeer;
        }

        public JsonRpcPeer Client { get; }

        public JsonRpcPeer Server { get; }

        public static async Task<DuplexFixture> CreateAsync(CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync(cancellationToken);
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            var server = await acceptTask;

            var clientPeer = new JsonRpcPeer(client.GetStream());
            var serverPeer = new JsonRpcPeer(server.GetStream());
            clientPeer.Start();
            serverPeer.Start();
            return new DuplexFixture(listener, client, server, clientPeer, serverPeer);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
            _client.Dispose();
            _server.Dispose();
            _listener.Stop();
        }
    }
}
