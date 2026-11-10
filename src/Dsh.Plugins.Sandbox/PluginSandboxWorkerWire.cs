using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Dsh.Plugins.Sandbox;

/** JSON values use the protocol's single frame-size boundary. */
internal static class PluginSandboxWorkerWire
{
    internal static object? Configuration(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(
            static property => property.Name, static property => Configuration(property.Value), StringComparer.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Select(Configuration).ToArray(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => Number(value),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => throw new InvalidOperationException("The worker configuration contains an unsupported JSON value."),
    };

    private static object Number(JsonElement value)
    {
        if (value.TryGetInt32(out var integer))
            return integer;
        if (value.TryGetInt64(out var wideInteger))
            return wideInteger;
        if (value.TryGetUInt64(out var unsignedInteger))
            return unsignedInteger;
        var floating = value.GetDouble();
        if (value.TryGetDecimal(out var fraction) && (double)fraction == floating)
            return fraction;
        return floating;
    }

    internal static JsonElement Serialize<T>(T value, JsonTypeInfo<T> type)
        => JsonSerializer.SerializeToElement(value, type);

    internal static T Read<T>(JsonElement? value, JsonTypeInfo<T> type)
        => value is { } element
            ? element.Deserialize(type) ?? throw new InvalidOperationException("Missing worker request payload.")
            : throw new InvalidOperationException("Missing worker request payload.");

    internal static JsonElement Parse(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
