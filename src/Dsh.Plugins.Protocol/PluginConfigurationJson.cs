using System.Collections;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Plugins;

/// <summary>Serializes raw plugin configuration values without YAML, reflection or runtime dependencies.</summary>
public static class PluginConfigurationJson
{
    /// <summary>Null and empty mappings retain the SDK's absent-configuration empty-string contract.</summary>
    public static string Write(object? value)
    {
        if (value is null
            || value is IReadOnlyDictionary<string, object?> { Count: 0 }
            || value is IDictionary<string, object?> { Count: 0 }
            || value is IDictionary { Count: 0 })
            return "";
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        WriteValue(writer, value);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case bool flag: writer.WriteBooleanValue(flag); break;
            case string text: writer.WriteStringValue(text); break;
            case sbyte number: writer.WriteNumberValue(number); break;
            case byte number: writer.WriteNumberValue(number); break;
            case short number: writer.WriteNumberValue(number); break;
            case ushort number: writer.WriteNumberValue(number); break;
            case int number: writer.WriteNumberValue(number); break;
            case uint number: writer.WriteNumberValue(number); break;
            case long number: writer.WriteNumberValue(number); break;
            case ulong number: writer.WriteNumberValue(number); break;
            case float number: writer.WriteNumberValue(number); break;
            case double number: writer.WriteNumberValue(number); break;
            case decimal number: writer.WriteNumberValue(number); break;
            case JsonElement element: element.WriteTo(writer); break;
            case JsonDocument document: document.RootElement.WriteTo(writer); break;
            case JsonNode node: node.WriteTo(writer); break;
            case IReadOnlyDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var (key, item) in map)
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, item);
                }
                writer.WriteEndObject();
                break;
            case IDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var (key, item) in map)
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, item);
                }
                writer.WriteEndObject();
                break;
            case IDictionary map:
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in map)
                {
                    if (entry.Key is not string key)
                        throw new NotSupportedException("plugin configuration mapping keys must be strings");
                    writer.WritePropertyName(key);
                    WriteValue(writer, entry.Value);
                }
                writer.WriteEndObject();
                break;
            case IEnumerable list:
                writer.WriteStartArray();
                foreach (var item in list)
                    WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                throw new NotSupportedException("plugin configuration must contain only JSON-compatible primitive, mapping or list values");
        }
    }
}
