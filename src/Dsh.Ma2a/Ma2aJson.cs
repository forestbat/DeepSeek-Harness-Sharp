using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Ma2a;

internal static class Ma2aJson
{
    internal static JsonElement Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            ValidateMembers(document.RootElement);
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw new Ma2aProtocolException("invalid_frame", "The local response is not valid JSON."); }
    }

    private static void ValidateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new Ma2aProtocolException("invalid_frame", "The local response contains duplicate fields.");
                ValidateMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateMembers(item);
    }

    internal static void ValidateResponse(JsonElement response, JsonObject command)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("version", out var version)
            || !version.TryGetInt32(out var number) || number != Ma2aClient.ApiVersion)
            throw new Ma2aProtocolException("version_mismatch");
        if (response.TryGetProperty("error", out var error))
        {
            if (error.ValueKind != JsonValueKind.String || !response.TryGetProperty("remediation", out var remediation)
                || remediation.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new Ma2aProtocolException("invalid_frame");
            throw new Ma2aProtocolException(error.GetString()!, remediation.GetString());
        }
        ValidateSuccess(response, command);
    }

    private static void ValidateSuccess(JsonElement response, JsonObject command)
    {
        // Stream upgrades reserve replay identity in the subsequent service open frame.
        var expected = command["operation"]?.GetValue<string>() == "stream_open" ? null : command["request_id"]?.GetValue<string>();
        if (!response.TryGetProperty("request_id", out var requestId) || requestId.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
            || requestId.GetString() != expected || !response.TryGetProperty("revision", out var revision) || !revision.TryGetUInt64(out _)
            || !response.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !result.TryGetProperty("payload", out _))
            throw new Ma2aProtocolException("invalid_frame", "The local success envelope is malformed or belongs to a different request.");
    }

    internal static bool IsHex(string? text, int? bytes = null)
        => text is not null && text.Length > 0 && text.Length % 2 == 0 && (bytes is null || text.Length == bytes * 2)
            && text.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f');
}
