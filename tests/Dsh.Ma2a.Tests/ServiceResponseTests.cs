using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Dsh.Ma2a.Services;

namespace Dsh.Ma2a.Tests;

public sealed class ServiceResponseTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    public async Task FilesReadAllowsShortResultsWhenTheDeclaredCountMatches(int count, bool eof)
    {
        var frames = new List<Ma2aServiceFrame> { Cbor(Ma2aFrameKind.Accept, new JsonArray()) };
        if (count > 0) frames.Add(Data(1, new byte[count]));
        frames.Add(Success(new JsonArray(2, count, eof)));
        var (session, stream) = Create("files", Ma2aServiceRequests.Read("data:/file", length: 4), frames.ToArray());
        await using (session)
        using (stream)
        {
            await DrainAsync(session);
            Assert.True(session.IsTerminal);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task FilesReadRejectsSuccessWhoseDeclaredCountDiffersFromReceivedBytes(int declared)
    {
        var scenario = Create("files", Ma2aServiceRequests.Read("data:/file", length: 4),
            Cbor(Ma2aFrameKind.Accept, new JsonArray()), Data(1, [0, 255]), Success(new JsonArray(2, declared, true)));
        await AssertFailureAsync(scenario, 32);
    }

    [Fact]
    public async Task FilesReadRejectsDataBeyondTheRequestedBound()
        => await AssertFailureAsync(Create("files", Ma2aServiceRequests.Read("data:/file", length: 2),
            Cbor(Ma2aFrameKind.Accept, new JsonArray()), Data(1, [0, 255, 128]), Success(new JsonArray(2, 3, true))), 32);

    [Fact]
    public async Task FilesReadRejectsMoreThanTheNativeSmallFileBound()
        => await AssertFailureAsync(Create("files", Ma2aServiceRequests.Read("data:/file"),
            Cbor(Ma2aFrameKind.Accept, new JsonArray()), Data(1, new byte[65_536]), Data(1, [255])), 25);

    [Fact]
    public async Task FilesReadTerminalWithoutDataMustDeclareZeroBytes()
        => await AssertFailureAsync(Create("files", Ma2aServiceRequests.Read("data:/file"),
            Success(new JsonArray(2, 1, true))), 32);

    [Theory]
    [InlineData("[2,null,true]")]
    [InlineData("[2,1,null]")]
    [InlineData("[2,\"1\",true]")]
    [InlineData("[2,4294967296,true]")]
    public async Task MalformedFilesReadResultClosesTheSession(string result)
        => await AssertFailureAsync(Create("files", Ma2aServiceRequests.Read("data:/file"),
            Success(JsonNode.Parse(result))), 2);

    [Fact]
    public async Task FilesReadAllowsEmptyCachedTerminalWithoutAdmission()
    {
        var (session, stream) = Create("files", Ma2aServiceRequests.Read("data:/file"), Success(new JsonArray(2, 0, true)));
        await using (session)
        using (stream)
        {
            await DrainAsync(session);
            Assert.True(session.IsTerminal);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task FilesReadRejectsOtherDataChannels(byte channel)
        => await AssertFailureAsync(Create("files", Ma2aServiceRequests.Read("data:/file"),
            Cbor(Ma2aFrameKind.Accept, new JsonArray()), Data(channel, [255])), 2);

    [Theory]
    [InlineData(Ma2aFrameKind.End)]
    [InlineData(Ma2aFrameKind.Control)]
    public async Task FilesReadRejectsFramesTheNativeFilesCallerDoesNotAccept(Ma2aFrameKind kind)
        => await AssertFailureAsync(Create("files", Ma2aServiceRequests.Read("data:/file"),
            Cbor(Ma2aFrameKind.Accept, new JsonArray()), new Ma2aServiceFrame(kind, new byte[] { 1 })), 2);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task PartialReadFailurePreservesTheRemoteErrorInsteadOfReportingAnIntegrityMismatch(int count)
    {
        var (session, stream) = Create("files", Ma2aServiceRequests.Read("data:/file", length: 2),
            Cbor(Ma2aFrameKind.Accept, new JsonArray()), Data(1, new byte[count]),
            Cbor(Ma2aFrameKind.Outcome, new JsonObject { ["0"] = 26, ["1"] = null }));
        await using (session)
        using (stream)
        {
            var error = await Assert.ThrowsAsync<Ma2aServiceException>(() => DrainAsync(session));
            Assert.Equal(26, error.Status);
            Assert.True(session.IsTerminal);
            Assert.False(stream.CanRead);
        }
    }

    [Fact]
    public async Task PartialReadEofWithoutOutcomeIsUnknownAndReleasesTheStream()
        => await AssertFailureAsync(Create("files", Ma2aServiceRequests.Read("data:/file"),
            Cbor(Ma2aFrameKind.Accept, new JsonArray()), Data(1, [255])), 10);

    [Fact]
    public async Task RejectionRemainsAKnownTerminalAndReleasesTheStream()
    {
        var (session, stream) = Create("service", Call(), new Ma2aServiceFrame(Ma2aFrameKind.Reject, new byte[] { 3 }));
        await using (session)
        using (stream)
        {
            var error = await Assert.ThrowsAsync<Ma2aServiceException>(() => session.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(3, error.Status);
            Assert.True(error.Rejected);
            Assert.True(session.IsTerminal);
            Assert.False(stream.CanRead);
        }
    }

    [Fact]
    public async Task HostedCallAcceptsMaximumOpaqueValuesAndBothOutputChannels()
    {
        byte[] control = [255, 0, 192, 255];
        var result = Ma2aCbor.Bytes(new byte[Ma2aServiceRequests.MaximumHostedCallResultBytes]);
        var (session, stream) = Create("service", Call(),
            Cbor(Ma2aFrameKind.Accept, Ma2aCbor.Bytes(new byte[8_192])),
            new Ma2aServiceFrame(Ma2aFrameKind.Control, control), Data(1, [0, 255]), Data(2, [128]),
            new Ma2aServiceFrame(Ma2aFrameKind.End, new byte[] { 1 }),
            new Ma2aServiceFrame(Ma2aFrameKind.End, new byte[] { 2 }), Success(result));
        await using (session)
        using (stream)
        {
            await session.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(control, (await session.ReadAsync(TestContext.Current.CancellationToken)).Payload.ToArray());
            await DrainAsync(session);
            Assert.True(session.IsTerminal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostedCallRejectsNonBytesAndOversizedAdmission(bool oversized)
        => await AssertFailureAsync(Create("service", Call(), Cbor(Ma2aFrameKind.Accept,
            oversized ? Ma2aCbor.Bytes(new byte[8_193]) : new JsonArray())), 2);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostedCallRejectsNonBytesAndOversizedSuccess(bool oversized)
        => await AssertFailureAsync(Create("service", Call(), Success(
            oversized ? Ma2aCbor.Bytes(new byte[Ma2aServiceRequests.MaximumHostedCallResultBytes + 1]) : new JsonArray())), 2);

    [Theory]
    [InlineData(16_384, true)]
    [InlineData(16_385, false)]
    public async Task HostedControlKeepsItsRawFrameBound(int size, bool valid)
    {
        var bytes = Enumerable.Repeat((byte)255, size).ToArray();
        var scenario = Create("service", Call(), Cbor(Ma2aFrameKind.Accept, Ma2aCbor.Bytes([])),
            new Ma2aServiceFrame(Ma2aFrameKind.Control, bytes), Success(Ma2aCbor.Bytes([])));
        if (!valid) { await AssertFailureAsync(scenario, 2); return; }
        var (session, stream) = scenario;
        await using (session)
        using (stream)
        {
            await session.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(bytes, (await session.ReadAsync(TestContext.Current.CancellationToken)).Payload.ToArray());
            await DrainAsync(session);
        }
    }

    [Theory]
    [InlineData(Ma2aFrameKind.Data)]
    [InlineData(Ma2aFrameKind.End)]
    public async Task HostedCallRejectsInboundInputChannel(Ma2aFrameKind kind)
        => await AssertFailureAsync(Create("service", Call(), Cbor(Ma2aFrameKind.Accept, Ma2aCbor.Bytes([])),
            new Ma2aServiceFrame(kind, kind == Ma2aFrameKind.Data ? new byte[] { 0, 255 } : new byte[] { 0 })), 2);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task HostedCallRejectsWrongOutgoingDirectionAsCallerMisuse(byte channel)
    {
        var (session, stream) = Create("service", Call(), Cbor(Ma2aFrameKind.Accept, Ma2aCbor.Bytes([])));
        await using (session)
        using (stream)
        {
            await session.ReadAsync(TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendDataAsync(channel, new byte[] { 255 }, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.EndAsync(channel, TestContext.Current.CancellationToken));
            Assert.True(stream.CanRead);
            Assert.False(session.IsTerminal);
        }
    }

    [Fact]
    public async Task DataWritesPreserveBytesAcrossTheMaximumFrameBoundary()
    {
        const int tailLength = 3;
        var data = new byte[Ma2aServiceSession.MaximumDataLength + tailLength];
        new Random(0).NextBytes(data);
        var (session, stream) = Create("service", Call(), Cbor(Ma2aFrameKind.Accept, Ma2aCbor.Bytes([])));
        await using (session)
        using (stream)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await session.ReadAsync(cancellationToken);
            var start = stream.Position;
            await session.SendDataAsync(0, data, cancellationToken);
            await session.EndAsync(cancellationToken: cancellationToken);

            stream.Position = start;
            var first = await Ma2aServiceFraming.ReadAsync(stream, cancellationToken);
            var tail = await Ma2aServiceFraming.ReadAsync(stream, cancellationToken);
            var end = await Ma2aServiceFraming.ReadAsync(stream, cancellationToken);
            Assert.Equal(Ma2aFrameKind.Data, first.Kind);
            Assert.Equal((byte)0, first.Channel);
            Assert.Equal(data[..Ma2aServiceSession.MaximumDataLength], first.Data.ToArray());
            Assert.Equal(Ma2aFrameKind.Data, tail.Kind);
            Assert.Equal((byte)0, tail.Channel);
            Assert.Equal(data[^tailLength..], tail.Data.ToArray());
            Assert.Equal(Ma2aFrameKind.End, end.Kind);
            Assert.Equal((byte)0, end.Channel);
            Assert.Equal(stream.Length, stream.Position);
        }
    }

    [Fact]
    public async Task CancellingInsideADataFrameClosesTheStreamBeforeAQueuedFrameCanWrite()
    {
        using var stream = new StalledDataWriteStream();
        await using var session = Create(stream, "service", Call(), Cbor(Ma2aFrameKind.Accept, Ma2aCbor.Bytes([])));
        var cancellationToken = TestContext.Current.CancellationToken;
        await session.ReadAsync(cancellationToken);
        var start = stream.Position;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var write = session.SendDataAsync(0, new byte[Ma2aServiceSession.MaximumDataLength], cancellation.Token);
        Assert.Same(stream.DataWriteStarted.Task, await Task.WhenAny(stream.DataWriteStarted.Task, write).WaitAsync(cancellationToken));
        var partialLength = stream.Position;
        var end = session.EndAsync(cancellationToken: cancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => end);
        Assert.False(stream.CanWrite);
        Assert.True(partialLength > start);
        Assert.Equal(partialLength, stream.ToArray().LongLength);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostedDescribeKeepsItsStructuredResultAndCallKeepsItsCachedTerminal(bool describe)
    {
        var request = describe ? Ma2aServiceRequests.Describe() : Call();
        JsonNode result = describe ? new JsonArray(new JsonArray(), null) : Ma2aCbor.Bytes([]);
        var (session, stream) = Create("service", request, Success(result));
        await using (session)
        using (stream)
        {
            var frame = await session.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(Ma2aFrameKind.Outcome, frame.Kind);
            Assert.True(JsonNode.DeepEquals(result, frame.DecodeCbor()!["1"]));
            Assert.True(session.IsTerminal);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadAsync(TestContext.Current.CancellationToken));
            Assert.True(stream.CanRead);
        }
    }

    [Theory]
    [InlineData("ff")]
    [InlineData("a10000")]
    [InlineData("a200f601f6")]
    [InlineData("a200616101f6")]
    [InlineData("a200000100")]
    public async Task MalformedRemoteOutcomeIsAProtocolFailureAndClosesTheSession(string hex)
        => await AssertFailureAsync(Create("service", Call(),
            new Ma2aServiceFrame(Ma2aFrameKind.Outcome, Convert.FromHexString(hex))), 2);

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    public void AgentMessagePreservesValidTextIncludingEmptyContent(string text)
        => Assert.Equal(text, Ma2aServiceRequests.AgentMessage(text)[1]![0]![1]!.GetValue<string>());

    [Fact]
    public void AgentMessageUsesItsOwnNativeUtf8Bound()
    {
        var text = new string('文', Ma2aServiceRequests.MaximumAgentMessageBytes / Encoding.UTF8.GetByteCount("文"));
        Assert.Equal(text, Ma2aServiceRequests.AgentMessage(text)[1]![0]![1]!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => Ma2aServiceRequests.AgentMessage($"{text}a"));
        Assert.Throws<ArgumentException>(() => Ma2aServiceRequests.AgentMessage("a\0b"));
    }

    [Fact]
    public void RemotePathBoundsComponentBytesWithoutChargingForTheRootAndSeparators()
    {
        var root = new string('r', 64);
        var component = $"{new string('文', 21)}a";
        var path = $"{root}:/{string.Join('/', Enumerable.Repeat(component, 64))}";
        Assert.Equal(Ma2aServiceRequests.MaximumRemotePathTextBytes, Encoding.UTF8.GetByteCount(path));
        var decoded = Ma2aCbor.Decode(Ma2aCbor.Encode(Ma2aServiceRequests.Path(path)))!.AsArray();
        Assert.Equal(root, decoded[0]!.GetValue<string>());
        Assert.Equal(64, decoded[1]!.AsArray().Count);
        Assert.Equal(component, decoded[1]![63]!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => Ma2aServiceRequests.Path($"{path}a"));
    }

    [Theory]
    [InlineData("data:/..")]
    [InlineData("data:/a//b")]
    [InlineData("data:/a/./b")]
    [InlineData("data:/a\\b")]
    public void RemotePathKeepsItsTraversalAndComponentRules(string path)
        => Assert.Throws<ArgumentException>(() => Ma2aServiceRequests.Path(path));

    private static JsonArray Call() => Ma2aServiceRequests.ServiceCall("example", 1, "echo", []);
    private static Ma2aServiceFrame Cbor(Ma2aFrameKind kind, JsonNode? value) => new(kind, Ma2aCbor.Encode(value));
    private static Ma2aServiceFrame Success(JsonNode? result)
        => Cbor(Ma2aFrameKind.Outcome, new JsonObject { ["0"] = 0, ["1"] = result });
    private static Ma2aServiceFrame Data(byte channel, byte[] bytes)
    {
        byte[] payload = [channel, ..bytes];
        return new Ma2aServiceFrame(Ma2aFrameKind.Data, payload);
    }

    private static (Ma2aServiceSession Session, MemoryStream Stream) Create(string service, JsonNode request, params Ma2aServiceFrame[] frames)
    {
        var stream = new MemoryStream();
        return (Create(stream, service, request, frames), stream);
    }

    private static Ma2aServiceSession Create(MemoryStream stream, string service, JsonNode request, params Ma2aServiceFrame[] frames)
    {
        // Synthetic protocol boundary inputs exercise the production codec and session, without a replacement Runtime.
        Span<byte> header = stackalloc byte[5];
        foreach (var frame in frames)
        {
            header[0] = (byte)frame.Kind;
            BinaryPrimitives.WriteUInt32BigEndian(header[1..], (uint)frame.Payload.Length);
            stream.Write(header);
            stream.Write(frame.Payload.Span);
        }
        stream.Position = 0;
        return new Ma2aServiceSession(stream, service, request);
    }

    private sealed class StalledDataWriteStream : MemoryStream
    {
        public TaskCompletionSource DataWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length < Ma2aServiceSession.MaximumDataLength)
                return base.WriteAsync(buffer, cancellationToken);
            DataWriteStarted.TrySetResult();
            return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        }
    }

    private static async Task DrainAsync(Ma2aServiceSession session)
    {
        while ((await session.ReadAsync(TestContext.Current.CancellationToken)).Kind != Ma2aFrameKind.Outcome) { }
    }

    private static async Task AssertFailureAsync((Ma2aServiceSession Session, MemoryStream Stream) scenario, byte status)
    {
        var (session, stream) = scenario;
        await using (session)
        using (stream)
        {
            var error = await Assert.ThrowsAsync<Ma2aServiceException>(() => DrainAsync(session));
            Assert.Equal(status, error.Status);
            Assert.False(error.Rejected);
            Assert.False(session.IsTerminal);
            Assert.False(stream.CanRead);
        }
    }
}
