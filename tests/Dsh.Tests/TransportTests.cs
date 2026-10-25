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

    [Theory]
    [InlineData("")]
    [InlineData("hello 世界")]
    public async Task WritesHeaderAndPayloadTogether(string text)
    {
        using var stream = new CountingWriteStream();
        var payload = Encoding.UTF8.GetBytes(text);
        await FrameCodec.WriteFrameAsync(stream, payload, Ct);
        Assert.Equal(1, stream.Writes);
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

    private sealed class CountingWriteStream : MemoryStream
    {
        public int Writes { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            return base.WriteAsync(buffer, cancellationToken);
        }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowRequest_DoesNotBlockOtherRequestsOrResponses(bool synchronousPrelude)
    {
        await using var fixture = await DuplexFixture.CreateAsync(Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.RequestHandler = async (message, token) =>
        {
            if (message.Method == "slow")
            {
                entered.TrySetResult();
                if (synchronousPrelude)
                    release.Task.WaitAsync(token).GetAwaiter().GetResult();
                else
                    await release.Task.WaitAsync(token);
            }
            return Parse("""{"ok":true}""");
        };
        fixture.Client.RequestHandler = async (_, token) =>
            await fixture.Client.RequestAsync("nested", cancellationToken: token);
        var notifications = new List<int>();
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.NotificationReceived += message =>
        {
            notifications.Add(message.Params!.Value.GetProperty("seq").GetInt32());
            if (notifications.Count == 3)
                notified.TrySetResult();
        };

        var slow = fixture.Client.RequestAsync("slow", cancellationToken: Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            var ping = await fixture.Client.RequestAsync("ping", cancellationToken: Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.True(ping!.Value.GetProperty("ok").GetBoolean());
            Assert.False(slow.IsCompleted);
            var nested = await fixture.Server.RequestAsync("question", cancellationToken: Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.True(nested!.Value.GetProperty("ok").GetBoolean());
            for (var sequence = 0; sequence < 3; sequence++)
                await fixture.Client.NotifyAsync("ordered", Parse($"{{\"seq\":{sequence}}}"), Ct);
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.Equal([0, 1, 2], notifications);
            Assert.False(slow.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.True((await slow)!.Value.GetProperty("ok").GetBoolean());
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
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.RequestHandler = async (_, token) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                return null;
            }
            finally
            {
                finished.TrySetResult();
            }
        };
        var pending = fixture.Client.RequestAsync("hang", cancellationToken: Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await fixture.Server.DisposeAsync();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await Assert.ThrowsAnyAsync<Exception>(async () => await pending);
    }

    [Fact]
    public async Task ClosingPeer_DoesNotWaitForAHandlerIgnoringCancellation()
    {
        await using var fixture = await DuplexFixture.CreateAsync(Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.RequestHandler = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            finished.TrySetResult();
            return Parse("""{"ok":true}""");
        };
        try
        {
            var pending = fixture.Client.RequestAsync("slow", cancellationToken: Ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await fixture.Server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.False(finished.Task.IsCompleted);
            await Assert.ThrowsAnyAsync<Exception>(async () => await pending);
        }
        finally
        {
            release.TrySetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        }
    }

    /** Larger than what loopback socket buffers absorb, below the frame limit: the write blocks while the server is not reading. */
    private const int BlockingPayloadChars = 15 * 1024 * 1024;

    [Fact]
    public async Task CancellingARequestWhileItsFrameIsBeingWritten_KeepsTheConnectionUsable()
    {
        await using var fixture = await DuplexFixture.CreateAsync(Ct, startServer: false);
        fixture.Server.RequestHandler = (_, _) => ValueTask.FromResult<JsonElement?>(Parse("""{"ok":true}"""));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var large = fixture.Client.RequestAsync("large", Parse($"\"{new string('x', BlockingPayloadChars)}\""), cancel.Token);
        await cancel.CancelAsync();
        fixture.Server.Start();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => large);
        var after = await fixture.Client.RequestAsync("ping", cancellationToken: Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.True(after!.Value.GetProperty("ok").GetBoolean());
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

        public static async Task<DuplexFixture> CreateAsync(CancellationToken cancellationToken, bool startServer = true)
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
            if (startServer)
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
