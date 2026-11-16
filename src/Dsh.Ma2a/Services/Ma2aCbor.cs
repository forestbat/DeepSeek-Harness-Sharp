using System.Formats.Cbor;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Ma2a.Services;

/// <summary>Lossless service values; byte strings use a single "$bytes" hexadecimal member.</summary>
public static class Ma2aCbor
{
    private const int MaximumDepth = 32;

    public static JsonObject Bytes(ReadOnlySpan<byte> value) => new() { ["$bytes"] = Convert.ToHexStringLower(value) };

    public static byte[] Encode(JsonNode? value)
    {
        var writer = new CborWriter(CborConformanceMode.Canonical);
        Write(writer, value, 0);
        return writer.Encode();
    }

    public static JsonNode? Decode(ReadOnlyMemory<byte> bytes)
    {
        var reader = new CborReader(bytes, CborConformanceMode.Canonical);
        var value = Read(reader, 0);
        if (reader.BytesRemaining != 0)
            throw new InvalidDataException("Trailing service CBOR bytes.");
        return value;
    }

    private static void Write(CborWriter writer, JsonNode? node, int depth)
    {
        if (depth > MaximumDepth)
            throw new InvalidDataException("Service CBOR nesting exceeds its bound.");
        switch (node)
        {
            case null: writer.WriteNull(); break;
            case JsonArray array:
                writer.WriteStartArray(array.Count);
                foreach (var item in array) Write(writer, item, depth + 1);
                writer.WriteEndArray();
                break;
            case JsonObject map: WriteMap(writer, map, depth); break;
            case JsonValue scalar: WriteScalar(writer, scalar); break;
            default: throw new InvalidDataException("Unsupported service CBOR value.");
        }
    }

    private static void WriteMap(CborWriter writer, JsonObject map, int depth)
    {
        if (map.Count == 1 && map.TryGetPropertyValue("$bytes", out var bytes))
        {
            writer.WriteByteString(Convert.FromHexString(bytes!.GetValue<string>()));
            return;
        }
        var keys = map.Select(pair => ulong.Parse(pair.Key, CultureInfo.InvariantCulture)).Order().ToArray();
        if (keys.Any(key => !map.ContainsKey(key.ToString(CultureInfo.InvariantCulture))) || keys.Distinct().Count() != map.Count)
            throw new InvalidDataException("Service maps require canonical unsigned integer keys.");
        writer.WriteStartMap(map.Count);
        foreach (var key in keys)
        {
            writer.WriteUInt64(key);
            Write(writer, map[key.ToString(CultureInfo.InvariantCulture)], depth + 1);
        }
        writer.WriteEndMap();
    }

    private static void WriteScalar(CborWriter writer, JsonValue value)
    {
        var element = JsonSerializer.SerializeToElement(value);
        switch (element.ValueKind)
        {
            case JsonValueKind.String: writer.WriteTextString(element.GetString()!); break;
            case JsonValueKind.True: writer.WriteBoolean(true); break;
            case JsonValueKind.False: writer.WriteBoolean(false); break;
            case JsonValueKind.Number: writer.WriteUInt64(element.GetUInt64()); break;
            default: throw new InvalidDataException("Service values require unsigned integers, strings, booleans or null.");
        }
    }

    private static JsonNode? Read(CborReader reader, int depth)
    {
        if (depth > MaximumDepth)
            throw new InvalidDataException("Service CBOR nesting exceeds its bound.");
        return reader.PeekState() switch
        {
            CborReaderState.UnsignedInteger => JsonValue.Create(reader.ReadUInt64()),
            CborReaderState.TextString => JsonValue.Create(reader.ReadTextString()),
            CborReaderState.ByteString => Bytes(reader.ReadByteString()),
            CborReaderState.Boolean => JsonValue.Create(reader.ReadBoolean()),
            CborReaderState.Null => ReadNull(reader),
            CborReaderState.StartArray => ReadArray(reader, depth),
            CborReaderState.StartMap => ReadMap(reader, depth),
            _ => throw new InvalidDataException("Unsupported or noncanonical service CBOR value.")
        };
    }

    private static JsonNode? ReadNull(CborReader reader)
    {
        reader.ReadNull();
        return null;
    }

    private static JsonArray ReadArray(CborReader reader, int depth)
    {
        var length = reader.ReadStartArray() ?? throw new InvalidDataException("Indefinite service array.");
        var array = new JsonArray();
        for (var index = 0; index < length; index++) array.Add(Read(reader, depth + 1));
        reader.ReadEndArray();
        return array;
    }

    private static JsonObject ReadMap(CborReader reader, int depth)
    {
        var length = reader.ReadStartMap() ?? throw new InvalidDataException("Indefinite service map.");
        var map = new JsonObject();
        for (var index = 0; index < length; index++)
        {
            var key = reader.ReadUInt64().ToString(CultureInfo.InvariantCulture);
            map.Add(key, Read(reader, depth + 1));
        }
        reader.ReadEndMap();
        return map;
    }
}
