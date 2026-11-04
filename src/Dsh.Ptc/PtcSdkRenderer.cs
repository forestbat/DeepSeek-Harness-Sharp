using System.Text;
using Dsh.Llm;
using Dsh.ScriptHost;

namespace Dsh.Ptc;

/**
 * Renders scope-visible tools as deterministic C# method declarations.
 * 参数名与参数描述已经在每个工具自己的 schema 里(每次请求都随工具定义发出), 这里只声明"有哪些方法、什么形状"。
 * 真正的约束来自脚本宿主: 程序由 Roslyn 针对生成的 tools 类编译, 方法名写错会以编译错误回到模型, 而不是靠提示词保证。
 */
public static class PtcSdkRenderer
{
    private const string Instructions = """
        Programmatic tool calling: `run_code` executes a C# program statement body.
        Native tools remain directly callable; use `run_code` when a program is useful for composing tool calls.
        The body may use `await` and may `return` a `JsonNode`; print with `Console.WriteLine`.
        Printed output and returned values form the program result; image outputs from tool calls are delivered separately for you to inspect.
        Every tool in scope is declared below; pass its arguments as a `JsonObject` whose keys are that tool's own schema properties.
        Tools whose names are not valid C# identifiers have no declaration: call them as `await tools.call("<name>", new JsonObject { ... })`.
        The program runs under a wall-clock timeout; a compile or runtime error is reported back to you so you can correct the program.
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
        if (PtcToolNaming.IsCallable(schema.Name))
        {
            builder.Append("    public static Task<JsonNode?> ").Append(schema.Name).Append("(JsonObject args);\n");
        }
        else
        {
            builder.Append("    // not a C# identifier — call as await tools.call(\"").Append(schema.Name).Append("\", args)\n");
        }
    }
}
