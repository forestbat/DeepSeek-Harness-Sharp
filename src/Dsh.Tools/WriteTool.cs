using System.Text;
using System.Text.Json;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tools;

public sealed record WriteResultValue(string Path, string Operation, string? Before, string After);

public static class WriteTool
{
    public const string ToolName = "write";

    private const string SectionText = "Use the write tool to create files or completely replace file contents. Existing files are overwritten, so read an existing file first (the default fs-observation-policy requires it) and prefer edit for targeted changes.";

    public static IDisposable Register(Context ctx)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal("tool:write", PromptOrders.ToolWrite, SectionText));
        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = "Create or fully replace a UTF-8 text file.",
            Parameters = ToolSchemas.ObjectSchema(
                new Dictionary<string, System.Text.Json.Nodes.JsonObject>
                {
                    ["file_path"] = ToolSchemas.StringParam("Path to write, resolved by the filesystem backend."),
                    ["content"] = ToolSchemas.StringParam("Full UTF-8 text content to write."),
                },
                "file_path", "content"),
            Output = new ToolOutputDefinition(
                ToolSchemas.Parse("""
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["path", "operation", "after"],
                      "properties": {
                        "path": { "type": "string" },
                        "operation": { "type": "string", "enum": ["create", "update"] },
                        "before": { "type": ["string", "null"] },
                        "after": { "type": "string" }
                      }
                    }
                    """),
                (_, value) =>
                {
                    var result = value.Deserialize<WriteResultValue>(DshJson.Options)
                        ?? throw new JsonException("write result value is malformed");
                    return [new TextBlock(FormatWriteOutput(result.Path, result.Operation))];
                }),
            Execute = Execute,
        });
        return new CompositeDisposable(section, registration);
    }

    internal static string FormatWriteOutput(string displayPath, string operation)
    {
        var verb = operation == "create" ? "Created" : "Updated";
        return $"<path>{displayPath}</path>\n<type>file</type>\n<content>\n{verb} file\n</content>";
    }

    private static async Task<object?> Execute(JsonElement args, ToolRunContext exec)
    {
        var filePath = args.GetProperty("file_path").GetString() ?? "";
        if (filePath.Trim().Length == 0)
            throw new ArgumentException("file_path must be a non-empty string");
        var content = args.GetProperty("content").GetString() ?? "";
        var target = WorkspacePath.Resolve(exec, filePath);
        exec.Signal.ThrowIfCancellationRequested();
        string? before = null;
        var operation = "create";
        if (File.Exists(target))
        {
            before = await File.ReadAllTextAsync(target, new UTF8Encoding(false, false), exec.Signal);
            operation = "update";
        }
        var directory = Path.GetDirectoryName(target);
        if (directory is not null)
            Directory.CreateDirectory(directory);
        var tempPath = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, content, new UTF8Encoding(false, false), exec.Signal);
            File.Move(tempPath, target, true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        return new WriteResultValue(target, operation, before, content);
    }
}
