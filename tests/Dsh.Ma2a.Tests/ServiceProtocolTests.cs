using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Dsh.Ma2a.Services;

namespace Dsh.Ma2a.Tests;

public sealed class ServiceProtocolTests
{
    [Fact]
    public void NativeFilesStatOpenMatchesCanonicalWireVector()
    {
        var bytes = Ma2aServiceSession.EncodeOpen(new string('2', 64), "files", new string('1', 32),
            Ma2aServiceRequests.Stat("data:/hello.txt"));
        var expected = Convert.FromHexString($"a5008201000150{new string('1', 32)}025820{new string('2', 64)}0302048200826464617461816968656c6c6f2e747874");
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void SharedServiceOperationsKeepFileTransferFormsOutOfStreamPreparation()
    {
        var directTransfers = Ma2aServiceOperations.All.Where(operation => operation.ToolName is null).ToArray();

        Assert.Equal(new[] { "download", "upload" }, directTransfers.Select(operation => operation.Name));
        Assert.All(directTransfers, operation =>
        {
            Assert.Equal("transfer", operation.Family);
            var prepared = operation.PrepareForm(new Dictionary<string, JsonNode?>
            {
                ["path"] = "data:/example", ["local_path"] = "example.bin", ["replace"] = true,
            });
            Assert.Null(prepared.Body);
            Assert.IsType<Ma2aServiceOperations.TransferIntent>(prepared.Intent);
        });
        Assert.False(directTransfers[0].IsMutation);
        Assert.True(directTransfers[1].IsMutation);
    }

    [Fact]
    public void ProcessLogCursorKeepsItsDifferentFormAndToolDefaults()
    {
        const string id = "00000000000000000000000000000001";
        var definition = Ma2aServiceOperations.All.Single(operation => operation.ToolName == "process.logs");
        var cursor = definition.FormFields.Single(field => field.Name == "cursor");
        var form = definition.PrepareForm(new Dictionary<string, JsonNode?>
        {
            ["process_id"] = id, ["cursor"] = cursor.InitialNumber,
        }).Body;
        var tool = definition.BuildToolRequest(new JsonObject { ["id"] = id });

        Assert.True(JsonNode.DeepEquals(Ma2aServiceRequests.ProcessLogs(id, cursor: 0), form));
        Assert.True(JsonNode.DeepEquals(Ma2aServiceRequests.ProcessLogs(id), tool));
        Assert.False(JsonNode.DeepEquals(tool, form));
    }

    [Fact]
    public void ByteStringsAndLargeUnsignedIntegersRemainLossless()
    {
        var value = new JsonArray(ulong.MaxValue, Ma2aCbor.Bytes(new byte[] { 0, 255, 128 }), "终端");
        var decoded = Ma2aCbor.Decode(Ma2aCbor.Encode(value));
        Assert.True(JsonNode.DeepEquals(value, decoded));
    }

    [Fact]
    public void ConstructedAndParsedScalarsRetainCanonicalTypesWithoutReflection()
    {
        var value = new JsonArray((byte)1, 24, long.MaxValue, ulong.MaxValue, "文本", true, false, null);
        var expected = Convert.FromHexString("880118181b7fffffffffffffff1bffffffffffffffff66e69687e69cacf5f4f6");
        Assert.Equal(expected, Ma2aCbor.Encode(value));
        Assert.Equal(expected, Ma2aCbor.Encode(JsonNode.Parse(value.ToJsonString())));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("18446744073709551616")]
    public void NonUnsignedJsonNumbersRemainInvalidServiceScalars(string json)
        => Assert.Throws<FormatException>(() => Ma2aCbor.Encode(JsonNode.Parse(json)));

    [Fact]
    public void NonUnsignedConstructedNumbersRemainInvalidServiceScalars()
    {
        Assert.Throws<FormatException>(() => Ma2aCbor.Encode(JsonValue.Create(-1L)));
        Assert.Throws<FormatException>(() => Ma2aCbor.Encode(JsonValue.Create(1.5m)));
    }

    [Fact]
    public void NativeTerminationPreservesTheUnsignedExitCodeBitPattern()
    {
        var decoded = Ma2aCbor.Decode(Convert.FromHexString("82001affffffff"));
        Assert.Equal((ulong)uint.MaxValue, decoded![1]!.GetValue<ulong>());
        Assert.Equal(Convert.FromHexString("82001affffffff"), Ma2aCbor.Encode(decoded));
    }

    [Theory]
    [InlineData("1817")]
    [InlineData("9f00ff")]
    [InlineData("0000")]
    [InlineData("a200000001")]
    public void MalformedOrNoncanonicalCborIsRejected(string encoded)
        => Assert.ThrowsAny<Exception>(() => Ma2aCbor.Decode(Convert.FromHexString(encoded)));

    [Theory]
    [InlineData("0400010002")]
    [InlineData("0700010001")]
    [InlineData("0200000000")]
    [InlineData("0900000001")]
    public async Task FrameBoundsAreCheckedBeforePayloadIsRead(string header)
    {
        await using var stream = new MemoryStream(Convert.FromHexString(header));
        await Assert.ThrowsAsync<InvalidDataException>(() => Ma2aServiceFraming.ReadAsync(stream, TestContext.Current.CancellationToken));
        Assert.Equal(5, stream.Position);
    }

    [Fact]
    public async Task IncompletePayloadCanBeCancelledWithoutAProtocolDeadline()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, TestContext.Current.CancellationToken);
        using var sender = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        await connect;
        await sender.GetStream().WriteAsync(new byte[] { 4, 0, 0, 0, 3, 1 }, TestContext.Current.CancellationToken);
        await using var input = client.GetStream();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var read = Ma2aServiceFraming.ReadAsync(input, cancellation.Token);
        Assert.False(read.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public async Task DataFrameRetainsItsChannelAndBinaryBytes()
    {
        await using var stream = new MemoryStream(new byte[] { 4, 0, 0, 0, 4, 2, 0, 255, 128 });
        var frame = await Ma2aServiceFraming.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal(Ma2aFrameKind.Data, frame.Kind);
        Assert.Equal(2, frame.Channel);
        Assert.Equal(new byte[] { 0, 255, 128 }, frame.Data.ToArray());
    }

    [Fact]
    public async Task OpaqueHostedControlRetainsNonCborBytes()
    {
        await using var stream = new MemoryStream(new byte[] { 6, 0, 0, 0, 4, 255, 0, 192, 255 });
        var frame = await Ma2aServiceFraming.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal(Ma2aFrameKind.Control, frame.Kind);
        Assert.Equal(new byte[] { 255, 0, 192, 255 }, frame.Payload.ToArray());
    }
}
