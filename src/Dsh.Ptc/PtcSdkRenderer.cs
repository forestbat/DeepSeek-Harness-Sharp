using System.Text;
using System.Text.Json.Nodes;
using Dsh.Llm;

namespace Dsh.Ptc;

/** 把某 scope 可见工具的 JSON Schema 投影成 C# 声明风味的 SDK 文本; 工具按名字典序, 输出字节级确定。 */
public static class PtcSdkRenderer
{
    private const int MaxTypeDepth = 4;

    private const string Instructions = """
        Programmatic tool calling: `run_code` executes a C# program statement body.
        Only `run_code` can be called directly; every other tool is callable only from inside the program.
        The body may use `await` and may `return` a `JsonNode`; print with `Console.WriteLine`. Only what you print or return is returned to you.
        A tool call looks like `await tools.bash(new JsonObject { ["command"] = "..." })`; it returns `JsonNode?` and throws `ToolCallError` (with a `ToolName` property) when the tool fails.
        The program runs under a wall-clock timeout; an unhandled exception is reported back to you as a failure so you can correct the program.
        """;

    public static string Render(IReadOnlyList<ToolSchema> schemas)
    {
        var ordered = schemas
            .Where(schema => schema.Name != PtcTransport.RunCodeName && PtcToolNaming.IsCallable(schema.Name))
            .OrderBy(schema => schema.Name, StringComparer.Ordinal)
            .ToList();
        var builder = new StringBuilder();
        builder.Append(Instructions);
        if (ordered.Count == 0)
            return builder.ToString();
        builder.Append('\n');
        builder.Append("static class tools\n{\n");
        foreach (var schema in ordered)
            AppendDeclaration(builder, schema);
        builder.Append("}\n");
        return builder.ToString();
    }

    private static void AppendDeclaration(StringBuilder builder, ToolSchema schema)
    {
        builder.Append('\n');
        builder.Append("    // ").Append(schema.Name).Append(" — ").Append(OneLine(schema.Description)).Append('\n');
        if (schema.Parameters["properties"] is JsonObject properties)
        {
            var required = (schema.Parameters["required"] as JsonArray)?
                .Select(node => node?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var (name, value) in properties.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var optional = required.Contains(name) ? "" : "?";
                builder.Append("    //   ").Append(name).Append(optional).Append(": ").Append(Project(value, MaxTypeDepth));
                if (value is JsonObject property && property["description"]?.GetValue<string>() is { Length: > 0 } description)
                    builder.Append(" — ").Append(OneLine(description));
                builder.Append('\n');
            }
        }
        builder.Append("    public static Task<JsonNode?> ").Append(schema.Name).Append("(JsonObject args);\n");
    }

    private static string Project(JsonNode? schema, int depth)
    {
        if (schema is not JsonObject node || depth <= 0)
            return "JsonNode";
        if (node["const"] is { } constant)
            return constant.ToJsonString();
        if (node["enum"] is JsonArray values)
            return Join(values.Select(value => value?.ToJsonString() ?? "null"));
        if (node["oneOf"] is JsonArray oneOf)
            return Join(oneOf.Select(value => Project(value, depth - 1)));
        if (node["anyOf"] is JsonArray anyOf)
            return Join(anyOf.Select(value => Project(value, depth - 1)));
        return node["type"]?.GetValue<string>() switch
        {
            "string" => "string",
            "integer" => "int",
            "number" => "double",
            "boolean" => "bool",
            "null" => "null",
            "array" => $"{Project(node["items"], depth - 1)}[]",
            "object" => ProjectObject(node, depth - 1),
            _ => "JsonNode",
        };
    }

    private static string ProjectObject(JsonObject node, int depth)
    {
        if (node["properties"] is not JsonObject properties)
            return "JsonObject";
        var required = (node["required"] as JsonArray)?
            .Select(value => value?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        var members = properties
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key}{(required.Contains(entry.Key) ? "" : "?")}: {Project(entry.Value, depth)}");
        return $"{{ {string.Join(", ", members)} }}";
    }

    private static string Join(IEnumerable<string> parts) => string.Join(" | ", parts);

    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
