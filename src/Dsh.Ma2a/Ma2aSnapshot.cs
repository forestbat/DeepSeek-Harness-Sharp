using System.Buffers;
using System.Text.Json;

namespace Dsh.Ma2a;

internal static class Ma2aSnapshot
{
    internal const int MaximumSnapshotBytes = 12 * 1024 * 1024;
    private const int FragmentBytes = 16_384;
    private const int BootIdBytes = 16;
    private static readonly HashSet<string> FragmentFields = ["type", "revision", "runtime_boot_id", "index", "last", "data_hex"];

    internal static async Task<JsonElement> ReadAsync(Ma2aConnection.ResponseReader reader, string operation, JsonElement first)
    {
        var expectedType = operation switch
        {
            "snapshot_fetch" => "snapshot", "space_list" => "spaces",
            _ => throw new Ma2aProtocolException("invalid_frame", "This operation cannot return snapshot fragments."),
        };
        var bytes = new ArrayBufferWriter<byte>(FragmentBytes);
        var stamp = ReadStamp(first);
        var fragment = first;
        ulong index = 0;
        while (true)
        {
            var last = Append(fragment, stamp, index, bytes);
            if (last) break;
            index = checked(index + 1);
            fragment = Ma2aJson.Parse(await reader.ReadAsync());
        }
        var response = Ma2aJson.Parse(bytes.WrittenMemory);
        ValidateAssembled(response, stamp, expectedType);
        return response;
    }

    private static (ulong Revision, string Boot) ReadStamp(JsonElement fragment)
    {
        if (!fragment.TryGetProperty("revision", out var revision) || !revision.TryGetUInt64(out var value)
            || !fragment.TryGetProperty("runtime_boot_id", out var boot) || boot.ValueKind != JsonValueKind.String
            || !Ma2aJson.IsHex(boot.GetString(), BootIdBytes))
            throw new Ma2aProtocolException("invalid_frame", "The snapshot stamp is invalid.");
        return (value, boot.GetString()!);
    }

    private static bool Append(JsonElement fragment, (ulong Revision, string Boot) stamp, ulong index, ArrayBufferWriter<byte> bytes)
    {
        if (fragment.ValueKind != JsonValueKind.Object || fragment.EnumerateObject().Count() != FragmentFields.Count
            || fragment.EnumerateObject().Any(property => !FragmentFields.Contains(property.Name))
            || fragment.GetProperty("type").GetString() != "snapshot_fragment" || ReadStamp(fragment) != stamp
            || !fragment.GetProperty("index").TryGetUInt64(out var actual) || actual != index)
            throw new Ma2aProtocolException("invalid_frame", "The snapshot fragments are not contiguous and consistently stamped.");
        var last = fragment.GetProperty("last");
        var data = fragment.GetProperty("data_hex");
        if (last.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || data.ValueKind != JsonValueKind.String)
            throw new Ma2aProtocolException("invalid_frame");
        var isLast = last.GetBoolean();
        var hex = data.GetString()!;
        if (!Ma2aJson.IsHex(hex) || hex.Length > FragmentBytes * 2 || (!isLast && hex.Length != FragmentBytes * 2))
            throw new Ma2aProtocolException("invalid_frame", "The snapshot fragment size or encoding is invalid.");
        var fragmentBytes = hex.Length / 2;
        var totalBytes = bytes.WrittenCount + fragmentBytes;
        if (totalBytes > MaximumSnapshotBytes)
            throw new Ma2aProtocolException("invalid_frame", "The assembled snapshot exceeds the response limit.");
        if (Convert.FromHexString(hex.AsSpan(), bytes.GetSpan(fragmentBytes), out _, out var written) != OperationStatus.Done)
            throw new Ma2aProtocolException("invalid_frame", "The snapshot fragment encoding is invalid.");
        bytes.Advance(written);
        return isLast;
    }

    private static void ValidateAssembled(JsonElement response, (ulong Revision, string Boot) stamp, string expectedType)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("revision", out var revision)
            || !revision.TryGetUInt64(out var value) || value != stamp.Revision
            || !response.TryGetProperty("runtime_boot_id", out var boot) || boot.GetString() != stamp.Boot
            || !response.TryGetProperty("result", out var result) || result.GetProperty("type").GetString() != expectedType)
            throw new Ma2aProtocolException("invalid_frame", "The assembled snapshot does not match its fragment stamp.");
        if (expectedType == "snapshot" && (!result.GetProperty("payload").GetProperty("revision").TryGetUInt64(out var payloadRevision) || payloadRevision != stamp.Revision))
            throw new Ma2aProtocolException("invalid_frame", "The snapshot payload revision does not match its envelope.");
    }

    internal static void ValidateStampedResponse(JsonElement response, string expectedType)
        => ValidateAssembled(response, ReadStamp(response), expectedType);
}
