using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Dsh.Boot;

/** Worker-side limits apply before protocol serialization or reverse RPC admission. */
internal static class PluginSandboxWorkerWire
{
    internal const int MaximumPayloadBytes = 512 * 1024;
    internal const int MaximumPackages = 128;
    internal const int MaximumTools = 256;
    internal const int MaximumRegistrationBytes = 512 * 1024;
    internal const int MaximumSchemaBytes = 32 * 1024;

    internal static void Text(string value, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > maximumBytes || Encoding.UTF8.GetByteCount(value) > maximumBytes)
            throw new InvalidOperationException("Plugin text exceeds the worker payload limit.");
    }

    internal static JsonElement Element(JsonElement value, int maximumBytes = MaximumPayloadBytes)
    {
        using var stream = new LimitedStream(maximumBytes);
        using (var writer = new Utf8JsonWriter(stream))
            value.WriteTo(writer);
        using var document = JsonDocument.Parse(stream.GetBuffer().AsMemory(0, (int)stream.Length));
        return document.RootElement.Clone();
    }

    internal static JsonElement Serialize<T>(T value, JsonTypeInfo<T> type)
    {
        using var stream = new LimitedStream(MaximumPayloadBytes);
        using (var writer = new Utf8JsonWriter(stream))
            JsonSerializer.Serialize(writer, value, type);
        using var document = JsonDocument.Parse(stream.GetBuffer().AsMemory(0, (int)stream.Length));
        return document.RootElement.Clone();
    }

    internal static T Read<T>(JsonElement? value, JsonTypeInfo<T> type)
        => value is { } element
            ? element.Deserialize(type) ?? throw new InvalidOperationException("Missing worker request payload.")
            : throw new InvalidOperationException("Missing worker request payload.");

    internal static JsonElement Parse(string value)
    {
        Text(value, MaximumPayloadBytes);
        using var document = JsonDocument.Parse(value);
        return Element(document.RootElement);
    }

    private sealed class LimitedStream(int maximumBytes) : MemoryStream
    {
        private void Check(int count)
        {
            if (count > maximumBytes - Position)
                throw new InvalidOperationException("Plugin JSON exceeds the worker payload limit.");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Check(1);
            base.WriteByte(value);
        }
    }
}
