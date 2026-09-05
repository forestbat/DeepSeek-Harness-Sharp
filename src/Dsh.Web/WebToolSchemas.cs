using System.Text.Json.Nodes;

namespace Dsh.Web;

internal static class WebToolSchemas
{
    public static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    public static JsonObject StringParam(string description)
    {
        return new JsonObject { ["type"] = "string", ["description"] = description };
    }

    public static JsonObject ObjectSchema(IReadOnlyDictionary<string, JsonObject> properties, params string[] required)
    {
        var propertiesNode = new JsonObject();
        foreach (var (name, schema) in properties)
            propertiesNode[name] = schema;
        var schemaNode = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = propertiesNode,
        };
        if (required.Length > 0)
            schemaNode["required"] = new JsonArray(required.Select(name => JsonValue.Create(name)).ToArray<JsonNode?>());
        return schemaNode;
    }
}
