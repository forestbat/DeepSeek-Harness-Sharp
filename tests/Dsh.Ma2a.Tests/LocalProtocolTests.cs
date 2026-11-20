using System.Buffers.Binary;
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
    private const string SnapshotBoot = "00000000000000000000000000000001";
    [Fact]
    public async Task FramesMatchThePrivateIpcWireFormat()
    {
        using var stream = new MemoryStream();
        await Ma2aConnection.WriteAsync(stream, 7, "{}"u8.ToArray(), CancellationToken.None);
        Assert.Equal("0000000200000000000000077B7D", Convert.ToHexString(stream.ToArray()));
        stream.Position = 0;
        Assert.Equal("{}", Encoding.UTF8.GetString(await Ma2aConnection.ReadAsync(stream, 7, CancellationToken.None)));
    }

    [Fact]
    public async Task RejectsMismatchedCorrelationBeforeReadingPayload()
    {
        using var stream = new MemoryStream(Convert.FromHexString("0000000200000000000000087B7D"));
        var error = await Assert.ThrowsAsync<Ma2aProtocolException>(() => Ma2aConnection.ReadAsync(stream, 7, CancellationToken.None));
        Assert.Equal("correlation_mismatch", error.Error);
    }

    [Fact]
    public async Task RejectsOversizedResponseBeforeAllocation()
    {
        using var stream = new MemoryStream(Convert.FromHexString("000100010000000000000007"));
        var error = await Assert.ThrowsAsync<Ma2aProtocolException>(() => Ma2aConnection.ReadAsync(stream, 7, CancellationToken.None));
        Assert.Equal("invalid_frame", error.Error);
    }

    [Fact]
    public void RejectsDuplicateResponseFields()
    {
        var error = Assert.Throws<Ma2aProtocolException>(() => Ma2aJson.Parse("""{"version":1,"version":1}"""u8.ToArray()));
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
        var response = await Ma2aSnapshot.ReadAsync(stream, 7, "snapshot_fetch", Ma2aJson.Parse(Encoding.UTF8.GetBytes(fragment.ToJsonString())), CancellationToken.None);
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
        await Assert.ThrowsAsync<Ma2aProtocolException>(() => Ma2aSnapshot.ReadAsync(stream, 7, "snapshot_fetch", Ma2aJson.Parse(Encoding.UTF8.GetBytes(fragment.ToJsonString())), CancellationToken.None));
    }

    [Fact]
    public async Task ReassemblesAllEntitiesAcrossResponseFramesBeyondTheSingleFrameLimit()
    {
        var (envelope, fragments) = FragmentedSnapshot();
        Assert.True(envelope.Length > Ma2aConnection.MaximumResponseBytes);
        Assert.True(fragments.Length > 2);
        using var stream = ResponseFrames(fragments.Skip(1));

        var response = await Ma2aSnapshot.ReadAsync(stream, SnapshotCorrelation, "snapshot_fetch",
            Ma2aJson.Parse(JsonSerializer.SerializeToUtf8Bytes(fragments[0])), TestContext.Current.CancellationToken);

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

        await Assert.ThrowsAnyAsync<IOException>(() => Ma2aSnapshot.ReadAsync(stream, SnapshotCorrelation, "snapshot_fetch",
            Ma2aJson.Parse(JsonSerializer.SerializeToUtf8Bytes(fragments[0])), TestContext.Current.CancellationToken));
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
        var fragments = envelope.Chunk(SnapshotFragmentBytes).Select((bytes, index) => new JsonObject
        {
            ["type"] = "snapshot_fragment", ["revision"] = SnapshotRevision, ["runtime_boot_id"] = SnapshotBoot,
            ["index"] = index, ["last"] = (index + 1) * SnapshotFragmentBytes >= envelope.Length,
            ["data_hex"] = Convert.ToHexStringLower(bytes),
        }).ToArray();
        return (envelope, fragments);
    }

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
}
