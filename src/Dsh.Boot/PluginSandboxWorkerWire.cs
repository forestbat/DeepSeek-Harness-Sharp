using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Dsh.Boot;

/** JSON values use the protocol's single frame-size boundary. */
internal static class PluginSandboxWorkerWire
{
    internal static JsonElement Element(JsonElement value) => value.Clone();

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
