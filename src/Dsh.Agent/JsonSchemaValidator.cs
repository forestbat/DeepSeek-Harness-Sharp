using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Dsh.Core;

namespace Dsh.Agent;

/** JSON Schema 校验:JsonSchema.Net 实现,不做 CLR 反射,AOT 与裁剪可用。 */
public static class JsonSchemaValidator
{
    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List };

    public static IReadOnlyList<string> Validate(JsonObject schema, JsonElement value, string path)
    {
        var results = Parse(schema).Evaluate(value, Options);
        if (results.IsValid)
            return [];
        var violations = new List<string>();
        Collect(results, path, violations);
        return violations;
    }

    public static void AssertSupported(JsonObject schema) => _ = Parse(schema);

    private static JsonSchema Parse(JsonObject schema) => JsonSchema.FromText(schema.ToJsonString());

    private static void Collect(EvaluationResults results, string path, List<string> violations)
    {
        if (results.Errors is { Count: > 0 } errors)
        {
            foreach (var (keyword, message) in errors)
            {
                var location = results.InstanceLocation.ToString();
                var detail = keyword.Length == 0 ? message : $"{keyword}: {message}";
                violations.Add($"{path}{location}: {detail}");
            }
        }
        foreach (var detail in results.Details ?? [])
            Collect(detail, path, violations);
    }
}
