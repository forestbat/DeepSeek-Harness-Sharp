using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Ma2a;

namespace Dsh.Ma2a.Tests;

public sealed class LocalProtocolTests
{
    private const int SnapshotEntityCount = 400;
    private const int SnapshotFragmentBytes = 16_384;
    private const ulong SnapshotRevision = 41;
    private const ulong SnapshotCorrelation = 7;
    private const string WindowsPipeSkipReason = "Windows named pipe identities require Windows.";
    private const string SnapshotBoot = "00000000000000000000000000000001";
    private static readonly TimeSpan WindowsPipeTestDeadline = TimeSpan.FromSeconds(15);

    [Fact]
    [Trait("Platform", "WindowsOnly")]
    public void WindowsPipeNameUsesTheFilesystemDirectoryIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip(WindowsPipeSkipReason);
            return;
        }
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"ControlState-{Guid.NewGuid():N}"));
        try
        {
            var statePath = directory.FullName;
            var name = Ma2aConnection.WindowsPipeName(statePath);
            Assert.NotNull(name);
            var extendedPath = $@"\\?\{statePath}";
            var alternateCase = Path.Combine(directory.Parent!.FullName, directory.Name.ToLowerInvariant());

            Assert.Equal(name, Ma2aConnection.WindowsPipeName(extendedPath));
            Assert.Equal(name, Ma2aConnection.WindowsPipeName($"{alternateCase}/"));
            Assert.NotEqual(name, Ma2aConnection.WindowsPipeName(directory.Parent.FullName));
        }
        finally { directory.Delete(); }
    }

    [Fact]
    [Trait("Platform", "WindowsOnly")]
    public async Task WindowsPipeConnectResolvesDirectoryCreatedWhileConnecting()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip(WindowsPipeSkipReason);
            return;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(WindowsPipeTestDeadline);
        var cancellationToken = deadline.Token;
        var root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"ControlState-{Guid.NewGuid():N}"));
        try
        {
            var statePath = Path.Combine(root.FullName, "MixedCaseState");
            var alias = Path.Combine(root.FullName, "mixedcasestate") + "/";
            Assert.False(Directory.Exists(alias));
            var connecting = Ma2aConnection.ConnectAsync(alias, cancellationToken);
            Assert.False(Directory.Exists(alias));
            Directory.CreateDirectory(statePath);
            var pipeName = Assert.IsType<string>(Ma2aConnection.WindowsPipeName(statePath));
            await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            await using var client = await connecting;
            await server.WaitForConnectionAsync(cancellationToken);
            var received = new byte["state ready"u8.Length];
            // Start reading before writing because the pipe may not buffer the payload.
            var reading = server.ReadExactlyAsync(received, cancellationToken).AsTask();
            var writing = client.WriteAsync("state ready"u8.ToArray(), cancellationToken).AsTask();
            await Task.WhenAll(reading, writing);
            Assert.Equal("state ready", Encoding.UTF8.GetString(received));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    [Trait("Platform", "WindowsOnly")]
    public async Task WindowsPipeNameResolvesTheStateBelowALocalJunction()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip(WindowsPipeSkipReason);
            return;
        }
        var root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"ControlState-{Guid.NewGuid():N}"));
        var cancellationToken = TestContext.Current.CancellationToken;
        var junction = Path.Combine(root.FullName, "via-junction");
        try
        {
            var state = Directory.CreateDirectory(Path.Combine(root.FullName, "target", "ControlState"));
            var command = new ProcessStartInfo("cmd.exe", "/D /C mklink /J via-junction target")
            {
                WorkingDirectory = root.FullName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(command) ?? throw new InvalidOperationException("Junction creation did not start.");
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            Assert.True(process.ExitCode == 0, await error);
            var expected = Ma2aConnection.WindowsPipeName(state.FullName);
            Assert.NotNull(expected);
            Assert.Equal(expected, Ma2aConnection.WindowsPipeName(Path.Combine(junction, state.Name)));
        }
        finally
        {
            if (Directory.Exists(junction))
                Directory.Delete(junction);
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FramesMatchThePrivateIpcWireFormat()
    {
        using var stream = new MemoryStream();
        await Ma2aConnection.WriteAsync(stream, 7, "{}"u8.ToArray(), CancellationToken.None);
        Assert.Equal("0000000200000000000000077B7D", Convert.ToHexString(stream.ToArray()));
        stream.Position = 0;
        using var reader = new Ma2aConnection.ResponseReader(stream, 7, TestContext.Current.CancellationToken);
        Assert.Equal("{}", Encoding.UTF8.GetString(await reader.ReadAsync(waitForReply: true)));
    }

    [Fact]
    public async Task ResponseUsesTheCallerTokenUntilTheReplyAndReusesOneTransportDeadline()
    {
        using var stream = new RecordingReadStream();
        var cancellationToken = TestContext.Current.CancellationToken;
        await Ma2aConnection.WriteAsync(stream, 7, "{}"u8.ToArray(), cancellationToken);
        await Ma2aConnection.WriteAsync(stream, 7, "[]"u8.ToArray(), cancellationToken);
        stream.Position = 0;
        using var reader = new Ma2aConnection.ResponseReader(stream, 7, cancellationToken);

        Assert.Equal("{}", Encoding.UTF8.GetString(await reader.ReadAsync(waitForReply: true)));
        Assert.Equal("[]", Encoding.UTF8.GetString(await reader.ReadAsync()));

        Assert.Equal(6, stream.ReadTokens.Count);
        Assert.Equal(cancellationToken, stream.ReadTokens[0]);
        var transportToken = stream.ReadTokens[1];
        Assert.True(transportToken.CanBeCanceled);
        Assert.NotEqual(cancellationToken, transportToken);
        Assert.All(stream.ReadTokens.Skip(1), token => Assert.Equal(transportToken, token));
    }

    [Fact]
    public async Task RejectsMismatchedCorrelationBeforeReadingPayload()
    {
        using var stream = new MemoryStream(Convert.FromHexString("0000000200000000000000087B7D"));
        using var reader = new Ma2aConnection.ResponseReader(stream, 7, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<Ma2aProtocolException>(() => reader.ReadAsync(waitForReply: true));
        Assert.Equal("correlation_mismatch", error.Error);
    }

    [Fact]
    public async Task RejectsOversizedResponseBeforeAllocation()
    {
        using var stream = new MemoryStream(Convert.FromHexString("000100010000000000000007"));
        using var reader = new Ma2aConnection.ResponseReader(stream, 7, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<Ma2aProtocolException>(() => reader.ReadAsync(waitForReply: true));
        Assert.Equal("invalid_frame", error.Error);
    }

    [Theory]
    [InlineData("""{"version":1,"version":1}""")]
    [InlineData("""{"result":[{"revision":1,"revisi\u006fn":2}]}""")]
    public void RejectsDuplicateResponseFields(string response)
    {
        var error = Assert.Throws<Ma2aProtocolException>(() => Ma2aJson.Parse(Encoding.UTF8.GetBytes(response)));
        Assert.Equal("invalid_frame", error.Error);
    }

    [Fact]
    public void PreservesApiErrorAndRemediation()
    {
        var response = Ma2aJson.Parse("""{"version":1,"error":"unauthorized","remediation":"Grant access first."}"""u8.ToArray());
        var error = Assert.Throws<Ma2aProtocolException>(() => Ma2aJson.ValidateResponse(response, new JsonObject()));
        Assert.Equal("unauthorized", error.Error);
        Assert.Equal("Grant access first.", error.Remediation);
    }

    [Fact]
    public void RejectsResponseForAnotherMutation()
    {
        var response = Ma2aJson.Parse("""{"version":1,"request_id":"00000000000000000000000000000001","revision":4,"result":{"type":"space_created","payload":{}}}"""u8.ToArray());
        var command = new JsonObject { ["request_id"] = "00000000000000000000000000000002" };
        Assert.Throws<Ma2aProtocolException>(() => Ma2aJson.ValidateResponse(response, command));
    }

    [Fact]
    public void StreamUpgradeAcknowledgementHasNoMutationRequestId()
    {
        var response = Ma2aJson.Parse("""{"version":1,"request_id":null,"revision":4,"result":{"type":"stream_opened","payload":{}}}"""u8.ToArray());
        var command = new JsonObject { ["operation"] = "stream_open", ["request_id"] = "00000000000000000000000000000001" };
        Ma2aJson.ValidateResponse(response, command);
    }

    [Fact]
    public void StreamUpgradeRejectsAnUnexpectedMutationRequestId()
    {
        var response = Ma2aJson.Parse("""{"version":1,"request_id":"00000000000000000000000000000001","revision":4,"result":{"type":"stream_opened","payload":{}}}"""u8.ToArray());
        var command = new JsonObject { ["operation"] = "stream_open", ["request_id"] = "00000000000000000000000000000001" };
        Assert.Throws<Ma2aProtocolException>(() => Ma2aJson.ValidateResponse(response, command));
    }

    [Fact]
    public void OrdinaryMutationStillRequiresItsRequestId()
    {
        var response = Ma2aJson.Parse("""{"version":1,"request_id":null,"revision":4,"result":{"type":"space_created","payload":{}}}"""u8.ToArray());
        var command = new JsonObject { ["operation"] = "space_create", ["request_id"] = "00000000000000000000000000000001" };
        Assert.Throws<Ma2aProtocolException>(() => Ma2aJson.ValidateResponse(response, command));
    }

    [Fact]
    public async Task ReassemblesAStampedSnapshotFragment()
    {
        const string envelope = """{"version":1,"request_id":null,"revision":4,"runtime_boot_id":"00000000000000000000000000000001","result":{"type":"snapshot","payload":{"revision":4}}}""";
        var fragment = new JsonObject
        {
            ["type"] = "snapshot_fragment", ["revision"] = 4,
            ["runtime_boot_id"] = "00000000000000000000000000000001", ["index"] = 0, ["last"] = true,
            ["data_hex"] = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(envelope)),
        };
        using var stream = new MemoryStream();
        using var reader = new Ma2aConnection.ResponseReader(stream, 7, TestContext.Current.CancellationToken);
        var response = await Ma2aSnapshot.ReadAsync(reader, "snapshot_fetch", Ma2aJson.Parse(Encoding.UTF8.GetBytes(fragment.ToJsonString())));
        Assert.Equal(4UL, response.GetProperty("result").GetProperty("payload").GetProperty("revision").GetUInt64());
    }

    [Fact]
    public async Task DiscardsSnapshotWithMismatchedPayloadRevision()
    {
        const string envelope = """{"version":1,"request_id":null,"revision":4,"runtime_boot_id":"00000000000000000000000000000001","result":{"type":"snapshot","payload":{"revision":3}}}""";
        var fragment = new JsonObject
        {
            ["type"] = "snapshot_fragment", ["revision"] = 4,
            ["runtime_boot_id"] = "00000000000000000000000000000001", ["index"] = 0, ["last"] = true,
            ["data_hex"] = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(envelope)),
        };
        using var stream = new MemoryStream();
        using var reader = new Ma2aConnection.ResponseReader(stream, 7, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<Ma2aProtocolException>(() => Ma2aSnapshot.ReadAsync(reader, "snapshot_fetch", Ma2aJson.Parse(Encoding.UTF8.GetBytes(fragment.ToJsonString()))));
    }

    [Fact]
    public async Task ReassemblesAllEntitiesAcrossResponseFramesBeyondTheSingleFrameLimit()
    {
        var (envelope, fragments) = FragmentedSnapshot();
        Assert.True(envelope.Length > Ma2aConnection.MaximumResponseBytes);
        Assert.True(fragments.Length > 2);
        using var stream = ResponseFrames(fragments.Skip(1));
        using var reader = new Ma2aConnection.ResponseReader(stream, SnapshotCorrelation, TestContext.Current.CancellationToken);

        var response = await Ma2aSnapshot.ReadAsync(reader, "snapshot_fetch",
            Ma2aJson.Parse(JsonSerializer.SerializeToUtf8Bytes(fragments[0])));

        Ma2aJson.ValidateResponse(response, new JsonObject { ["operation"] = "snapshot_fetch" });
        Assert.Equal(SnapshotRevision, response.GetProperty("revision").GetUInt64());
        Assert.Equal(SnapshotBoot, response.GetProperty("runtime_boot_id").GetString());
        var payload = response.GetProperty("result").GetProperty("payload");
        Assert.Equal(SnapshotRevision, payload.GetProperty("revision").GetUInt64());
        var spaces = payload.GetProperty("spaces");
        Assert.Equal(SnapshotEntityCount, spaces.GetArrayLength());
        Assert.Equal((SnapshotEntityCount - 1).ToString("x64"), spaces[SnapshotEntityCount - 1].GetProperty("space_id").GetString());
        Assert.Equal($"Space {SnapshotEntityCount - 1}", spaces[SnapshotEntityCount - 1].GetProperty("name").GetString());
        Assert.Equal(envelope.Length, Encoding.UTF8.GetByteCount(response.GetRawText()));
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData("snapshot_fetch", "snapshot", false)]
    [InlineData("snapshot_fetch", "snapshot", true)]
    [InlineData("space_list", "spaces", false)]
    [InlineData("space_list", "spaces", true)]
    public async Task SnapshotByteLimitAppliesToCompleteResponses(string operation, string resultType, bool oversized)
    {
        JsonNode payload = resultType == "snapshot" ? new JsonObject { ["revision"] = SnapshotRevision } : new JsonArray();
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["version"] = 1, ["request_id"] = null, ["revision"] = SnapshotRevision, ["runtime_boot_id"] = SnapshotBoot,
            ["result"] = new JsonObject { ["type"] = resultType, ["payload"] = payload },
        });
        var padded = new byte[Ma2aSnapshot.MaximumSnapshotBytes + (oversized ? 1 : 0)];
        envelope.CopyTo(padded, 0);
        padded.AsSpan(envelope.Length).Fill((byte)' ');
        var fragments = SnapshotFragments(padded);
        var first = Ma2aJson.Parse(JsonSerializer.SerializeToUtf8Bytes(fragments[0]));
        using var stream = ResponseFrames(fragments.Skip(1));
        using var reader = new Ma2aConnection.ResponseReader(stream, SnapshotCorrelation, TestContext.Current.CancellationToken);

        var read = Ma2aSnapshot.ReadAsync(reader, operation, first);
        if (oversized)
        {
            var error = await Assert.ThrowsAsync<Ma2aProtocolException>(() => read);
            Assert.Equal("invalid_frame", error.Error);
        }
        else
        {
            var response = await read;
            Assert.Equal(resultType, response.GetProperty("result").GetProperty("type").GetString());
            Assert.Equal(stream.Length, stream.Position);
        }
    }

    [Theory]
    [InlineData("missing_final")]
    [InlineData("missing_middle")]
    [InlineData("noncontiguous_index")]
    [InlineData("changed_boot")]
    public async Task DiscardsIncompleteOrInconsistentMultiFrameSnapshots(string corruption)
    {
        var (envelope, fragments) = FragmentedSnapshot();
        Assert.True(envelope.Length > Ma2aConnection.MaximumResponseBytes);
        var remaining = fragments.Skip(1).ToList();
        switch (corruption)
        {
            case "missing_final": remaining.RemoveAt(remaining.Count - 1); break;
            case "missing_middle": remaining.RemoveAt(0); break;
            case "noncontiguous_index": remaining[0]["index"] = 9; break;
            case "changed_boot": remaining[0]["runtime_boot_id"] = "00000000000000000000000000000002"; break;
            default: throw new ArgumentException("Unknown response fixture corruption.", nameof(corruption));
        }
        using var stream = ResponseFrames(remaining);
        using var reader = new Ma2aConnection.ResponseReader(stream, SnapshotCorrelation, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<IOException>(() => Ma2aSnapshot.ReadAsync(reader, "snapshot_fetch",
            Ma2aJson.Parse(JsonSerializer.SerializeToUtf8Bytes(fragments[0]))));
    }

    private static (byte[] Envelope, JsonObject[] Fragments) FragmentedSnapshot()
    {
        var payload = JsonNode.Parse("""
            {"revision":41,"endpoint":{"endpoint_id":"0000000000000000000000000000000000000000000000000000000000000001","runtime_version":"component-fixture","online":true},"spaces":[],"control_sync":{"peer_endpoint_ids":[]},"connections":[],"private_relay_candidates":[],"public_relay_fallbacks":[],"control_rounds":[],"observed_relay_state":{"private_relay_provider_running":false,"public_relay_connected":false},"reachability":{"state":"DegradedNoCommonHome","direct":false,"relayed":false},"recent_echo_summary":{"successes":0,"failures":0},"ui_auth":{"initialized":true,"password_set":false,"active_sessions":0}}
            """)!.AsObject();
        var spaces = payload["spaces"]!.AsArray();
        for (var index = 0; index < SnapshotEntityCount; index++)
            spaces.Add(new JsonObject
            {
                ["space_id"] = index.ToString("x64"), ["name"] = $"Space {index}", ["member_count"] = 1,
                ["generation"] = 0, ["chain_hash"] = new string('a', 64), ["revoked_count"] = 0,
            });
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["version"] = 1, ["request_id"] = null, ["revision"] = SnapshotRevision, ["runtime_boot_id"] = SnapshotBoot,
            ["result"] = new JsonObject { ["type"] = "snapshot", ["payload"] = payload },
        });
        return (envelope, SnapshotFragments(envelope));
    }

    private static JsonObject[] SnapshotFragments(byte[] envelope)
        => envelope.Chunk(SnapshotFragmentBytes).Select((bytes, index) => new JsonObject
        {
            ["type"] = "snapshot_fragment", ["revision"] = SnapshotRevision, ["runtime_boot_id"] = SnapshotBoot,
            ["index"] = index, ["last"] = (index + 1) * SnapshotFragmentBytes >= envelope.Length,
            ["data_hex"] = Convert.ToHexStringLower(bytes),
        }).ToArray();

    private static MemoryStream ResponseFrames(IEnumerable<JsonObject> fragments)
    {
        var stream = new MemoryStream();
        foreach (var fragment in fragments)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(fragment);
            var header = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(sizeof(uint)), SnapshotCorrelation);
            stream.Write(header);
            stream.Write(payload);
        }
        stream.Position = 0;
        return stream;
    }

    private sealed class RecordingReadStream : MemoryStream
    {
        public List<CancellationToken> ReadTokens { get; } = [];

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadTokens.Add(cancellationToken);
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
