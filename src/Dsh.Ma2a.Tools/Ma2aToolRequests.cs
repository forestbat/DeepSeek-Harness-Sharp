using System.Text.Json.Nodes;
using Dsh.Ma2a.Services;

namespace Dsh.Ma2a.Tools;

public static class Ma2aToolRequests
{
    private static readonly Dictionary<string, Ma2aServiceOperations.Operation> Builders = Ma2aServiceOperations.All
        .Where(operation => operation.ToolName is not null).ToDictionary(operation => operation.ToolName!, StringComparer.Ordinal);

    public static readonly string[] Operations = [.. Builders.Keys];

    public static JsonObject Prepare(string operation, JsonObject arguments)
    {
        if (!Builders.TryGetValue(operation, out var definition))
            throw new ArgumentException("Unknown native MA2A service operation.", nameof(operation));
        return new JsonObject { ["service"] = definition.Family, ["body"] = definition.BuildToolRequest(arguments) };
    }
}
