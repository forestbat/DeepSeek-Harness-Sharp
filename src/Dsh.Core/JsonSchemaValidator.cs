using System.Text.Json;
using System.Text.Json.Nodes;
using NJsonSchema;

namespace Dsh.Core;

public static class JsonSchemaValidator
{
    public static IReadOnlyList<string> Validate(JsonObject schema, JsonElement value, string path)
    {
        var jsonSchema = Parse(schema);
        return jsonSchema.Validate(value.GetRawText())
            .Select(error => $"{path}{error.Path}: {error.Kind}")
            .ToList();
    }

    public static void AssertSupported(JsonObject schema) => Parse(schema);

    private static JsonSchema Parse(JsonObject schema)
        => JsonSchema.FromJsonAsync(schema.ToJsonString()).GetAwaiter().GetResult();
}
