using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Lsp.Protocol;

public static class LspJson
{
    public static JsonElement? Property(JsonElement? element, string name)
        => element is { } value
            && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var property)
                ? property.Clone()
                : null;

    public static string? String(JsonElement? element, string name)
        => Property(element, name) is { } value && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? Int32(JsonElement? element, string name)
        => Property(element, name) is { } value
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;

    public static bool Boolean(JsonElement? element, string name)
        => Property(element, name) is { } value && value.ValueKind == JsonValueKind.True;

    public static IReadOnlyList<JsonElement> Items(JsonElement? element)
    {
        if (element is not { } array || array.ValueKind != JsonValueKind.Array)
            return [];
        var items = new List<JsonElement>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
            items.Add(item.Clone());
        return items;
    }

    public static LspPosition Position(JsonElement element)
        => new(
            Int32(element, "line") ?? 0,
            Int32(element, "character") ?? 0);

    public static LspPosition? Position(JsonElement? element)
        => element is { } value && value.ValueKind == JsonValueKind.Object ? Position(value) : null;

    public static LspRange Range(JsonElement element)
        => new(
            Position(Property(element, "start").GetValueOrDefault()),
            Position(Property(element, "end").GetValueOrDefault()));

    public static LspRange? Range(JsonElement? element)
        => element is { } value && value.ValueKind == JsonValueKind.Object ? Range(value) : null;

    public static JsonObject Object(params (string Name, JsonNode? Value)[] properties)
    {
        var result = new JsonObject();
        foreach (var (name, value) in properties)
            result[name] = value;
        return result;
    }

    public static JsonArray Array(IEnumerable<JsonNode?> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
            result.Add(value);
        return result;
    }

    public static JsonObject PositionNode(LspPosition position)
        => new()
        {
            ["line"] = position.Line,
            ["character"] = position.Character,
        };

    public static JsonObject RangeNode(LspRange range)
        => new()
        {
            ["start"] = PositionNode(range.Start),
            ["end"] = PositionNode(range.End),
        };

    public static JsonObject TextDocumentNode(string uri)
        => new() { ["uri"] = uri };
}
