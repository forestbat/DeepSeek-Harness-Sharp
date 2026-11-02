using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Ide.Surfaces;

public static class IdeTool
{
    public const string ToolName = "ide";

    public static IDisposable Register(Context ctx, IEditorService editors)
        => ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = "List configured installed editors, inspect workspace status, or open/reveal a workspace file in an editor selected by ID. Executable commands and argument templates come only from user settings.",
            Parameters = JsonNode.Parse("""
                {"type":"object","additionalProperties":false,"required":["action"],"properties":{
                  "action":{"type":"string","enum":["list","open","reveal","status"]},
                  "path":{"type":"string","description":"Workspace file, directory or project; omitted means the current workspace."},
                  "editor":{"type":"string","description":"Configured editor ID from list; omitted uses ordered selection."},
                  "line":{"type":"integer","minimum":1},"column":{"type":"integer","minimum":1},
                  "print":{"type":"boolean","description":"Return a copyable command without launching."},
                  "dry_run":{"type":"boolean","description":"Resolve and explain without launching."}
                }}
                """)!.AsObject(),
            Output = new(JsonNode.Parse("""{"type":"object"}""")!.AsObject(), (_, value) => [new TextBlock(value.GetRawText())]),
            Execute = (args, run) => ExecuteAsync(ctx, editors, args, run),
            IsConcurrencySafe = args => Action(args) is "list" or "status",
            WriteTargets = WriteTargets,
        });

    private static async Task<object?> ExecuteAsync(Context ctx, IEditorService editors, JsonElement args, ToolRunContext run)
    {
        var workspace = run.Agent?.Session.WorkingDirectory ?? Environment.CurrentDirectory;
        if (Action(args) == "list")
        {
            var installed = await editors.Catalog.DetectAsync(cancellationToken: run.Signal).ConfigureAwait(false);
            return new JsonObject { ["editors"] = JsonSerializer.SerializeToNode(installed, EditorJsonContext.Default.IReadOnlyListEditorInstallation) };
        }
        if (Action(args) == "status")
            return new JsonObject { ["status"] = IdeCommand.Status(ctx, editors, workspace, run.Agent?.Id.Value) };
        var input = new EditorCommandInput
        {
            Action = Action(args), Path = String(args, "path"), Editor = String(args, "editor"),
            Line = Position(args, "line"), Column = Position(args, "column"),
            HasPosition = args.TryGetProperty("line", out _) || args.TryGetProperty("column", out _),
            Print = Boolean(args, "print"), DryRun = Boolean(args, "dry_run"),
        };
        var result = await editors.LaunchAsync(input.Request(workspace, run.Agent?.Id.Value, EditorSessionContext.TurnId(ctx, run.Agent?.Session)), run.Signal).ConfigureAwait(false);
        return JsonSerializer.SerializeToNode(result, EditorJsonContext.Default.EditorResult)!.AsObject();
    }

    private static IReadOnlyList<string> WriteTargets(JsonElement args, ToolRunContext run)
    {
        if (Action(args) is "list" or "status" || Boolean(args, "print") || Boolean(args, "dry_run"))
            return [];
        var workspace = run.Agent?.Session.WorkingDirectory ?? Environment.CurrentDirectory;
        var path = String(args, "path");
        return path is null ? [workspace] : [workspace, Path.GetFullPath(path, workspace)];
    }

    private static string Action(JsonElement args)
        => String(args, "action") is { } action && action is "list" or "open" or "reveal" or "status"
            ? action : throw new ArgumentException("IDE action must be list, open, reveal or status.");

    private static string? String(JsonElement args, string name)
        => !args.TryGetProperty(name, out var value) ? null : value.ValueKind == JsonValueKind.String ? value.GetString()
            : throw new ArgumentException($"IDE {name} must be a string.");

    private static int Position(JsonElement args, string name)
        => !args.TryGetProperty(name, out var value) ? 1
            : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var position) && position > 0 ? position
            : throw new ArgumentException($"IDE {name} must be a positive integer.");

    private static bool Boolean(JsonElement args, string name)
        => !args.TryGetProperty(name, out var value) ? false : value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException($"IDE {name} must be boolean."),
        };
}
