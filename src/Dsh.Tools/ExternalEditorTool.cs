using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tools;

public sealed record ExternalEditorOpened(string Path, string Editor, int? ProcessId);

/** LLM hook for opening a file in the configured favorite editor. */
public static class ExternalEditorTool
{
    public const string ToolName = "open_external_editor";

    public static IDisposable Register(Context ctx)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        _ = ctx.Get<IExternalEditorLauncher>(IExternalEditorLauncher.ServiceName, false)
            ?? new ExternalEditorService(ctx);
        var launcher = ctx.Get<IExternalEditorLauncher>(IExternalEditorLauncher.ServiceName)!;
        return tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = "Open a file in the user's configured external editor. The first configured editor is the favorite; without configuration the platform default editor is used.",
            Parameters = ToolSchemas.ObjectSchema(
                new Dictionary<string, System.Text.Json.Nodes.JsonObject>
                {
                    ["file_path"] = ToolSchemas.StringParam("File path to open, resolved by the filesystem backend."),
                    ["editor"] = ToolSchemas.StringParam("Optional configured editor name; omitted means the favorite."),
                    ["line"] = ToolSchemas.NumberParam("Optional 1-based line."),
                    ["column"] = ToolSchemas.NumberParam("Optional 1-based column."),
                },
                "file_path"),
            Output = new ToolOutputDefinition(
                ToolSchemas.Parse("""
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["path", "editor"],
                      "properties": {
                        "path": { "type": "string" },
                        "editor": { "type": "string" },
                        "processId": { "type": ["integer", "null"] }
                      }
                    }
                    """),
                (_, value) => [new TextBlock(Format(value))]),
            Execute = (args, exec) => Execute(args, exec, launcher),
            WriteTargets = (args, exec) => WorkspacePath.WriteTarget(args, exec, "file_path"),
            IsConcurrencySafe = _ => false,
        });
    }

    private static Task<object?> Execute(JsonElement args, ToolRunContext exec, IExternalEditorLauncher launcher)
    {
        var path = WorkspacePath.Resolve(exec, Required(args, "file_path"));
        var editor = OptionalString(args, "editor");
        var line = OptionalPositiveInt(args, "line");
        var column = OptionalPositiveInt(args, "column");
        var launch = launcher.Launch(path, editor, line, column);
        return Task.FromResult<object?>(new ExternalEditorOpened(path, launch.Editor, launch.ProcessId));
    }

    private static string Format(JsonElement value)
        => $"Opened in {value.GetProperty("editor").GetString()}: {value.GetProperty("path").GetString()}";

    private static string Required(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
           && value.GetString() is { Length: > 0 } text
            ? text
            : throw new ArgumentException($"{name} must be a non-empty string");

    private static string? OptionalString(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? OptionalPositiveInt(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        var number = value.GetDouble();
        if (!double.IsFinite(number) || number % 1 != 0 || number < 1)
            throw new ArgumentException($"{name} must be a positive integer");
        return (int)number;
    }
}
