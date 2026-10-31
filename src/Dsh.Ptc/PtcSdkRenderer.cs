using System.Text;
using System.Text.Json.Nodes;
using Dsh.Llm;
using Dsh.ScriptHost;

namespace Dsh.Ptc;

/** Renders scope-visible tool schemas as deterministic C# SDK declarations. */
public static class PtcSdkRenderer
{
    private const int MaxTypeDepth = 4;

    private const string Instructions = """
        Programmatic tool calling: `run_code` executes a C# program statement body.
        Native tools remain directly callable; use `run_code` when a program is useful for composing tool calls.
        The body may use `await` and may `return` a `JsonNode`; print with `Console.WriteLine`.
        Printed output and returned values form the program result; image outputs from tool calls are delivered separately for you to inspect.
        Call a listed tool with `await tools.<name>(new JsonObject { ... })`; it returns `JsonNode?` and throws `ToolCallError` (with a `ToolName` property) when the tool fails.
        Tools whose names are not valid C# identifiers have no method; call them as `await tools.call("<name>", new JsonObject { ... })`.
        The program runs under a wall-clock timeout; an unhandled exception is reported back to you as a failure so you can correct the program.
        """;

    internal const string BatchingInstructions = """
        Prefer one `run_code` for multiple independent, non-interactive tool operations over separate native calls, even if native calls can run in parallel.
        Use the listed SDK methods inside that program; image outputs from those calls are delivered to you after it completes.
        Preserve dependencies, scopes, approvals, audit and cancellation, and stop for user decisions rather than batching across interaction boundaries.
        """;

    public static string Render(IReadOnlyList<ToolSchema> schemas, bool debugBatching)
    {
        var ordered = schemas
            .Where(schema => schema.Name != ToolRunCode.ToolName)
            .OrderBy(schema => schema.Name, StringComparer.Ordinal)
            .ToList();
        var builder = new StringBuilder();
        builder.Append(Instructions);
        if (debugBatching)
            builder.Append('\n').Append(BatchingInstructions);
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
        if (PtcToolNaming.IsCallable(schema.Name))
        {
            builder.Append("    public static Task<JsonNode?> ").Append(schema.Name).Append("(JsonObject args);\n");
        }
        else
        {
            builder.Append("    // not a C# identifier — call as await tools.call(\"").Append(schema.Name).Append("\", args)\n");
        }
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
