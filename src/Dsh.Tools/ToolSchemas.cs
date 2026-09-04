using System.Text.Json.Nodes;

namespace Dsh.Tools;

internal static class ToolSchemas
{
    public static JsonObject Parse(string json)
        => JsonNode.Parse(json)!.AsObject();

    public static JsonObject StringParam(string? description = null)
    {
        var node = new JsonObject { ["type"] = "string" };
        if (description is not null) node["description"] = description;
        return node;
    }

    public static JsonObject NumberParam(string? description = null)
    {
        var node = new JsonObject { ["type"] = "number" };
        if (description is not null) node["description"] = description;
        return node;
    }

    public static JsonObject BooleanParam(string? description = null)
    {
        var node = new JsonObject { ["type"] = "boolean" };
        if (description is not null) node["description"] = description;
        return node;
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

internal sealed class CompositeDisposable(params IReadOnlyList<IDisposable> disposables) : IDisposable
{
    public void Dispose()
    {
        foreach (var disposable in disposables)
            disposable.Dispose();
    }
}
