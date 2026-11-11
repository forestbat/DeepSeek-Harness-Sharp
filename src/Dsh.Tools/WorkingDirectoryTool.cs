using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Agent;

namespace Dsh.Tools;

public sealed record WorkingDirectoryValue(string Cwd);

/** working_directory: reads or changes the session working directory; the write permission root stays at the header cwd. */
public static class WorkingDirectoryTool
{
    public const string ToolName = "working_directory";

    private const string Description =
        "Read the current working directory, or change it with cd. "
        + "Relative paths in file tools and newly started shells and terminals resolve against it. "
        + "Existing persistent shells and terminals keep their own directories. "
        + "The write permission root stays at the session's original directory: writing outside it, or starting a shell outside it, needs user approval.";

    private const string SectionText =
        "Use working_directory with cd to work in another directory instead of repeating absolute paths; call it without arguments to see where relative paths resolve. "
        + "cd never widens write permission: writes outside the session's original directory still ask the user first.";

    private static readonly System.Text.Json.Nodes.JsonObject OutputSchema = ToolSchemas.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["cwd"],
          "properties": {
            "cwd": { "type": "string", "description": "Current absolute working directory." }
          }
        }
        """);

    public static IDisposable Register(Context ctx)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal($"tool:{ToolName}", SectionText));
        var context = systemPrompt.Context(new PromptContext("working-directory", assemble => ContextText(assemble.Agent?.Session)));
        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = Description,
            Parameters = ToolSchemas.ObjectSchema(
                new Dictionary<string, System.Text.Json.Nodes.JsonObject>
                {
                    ["cd"] = ToolSchemas.StringParam("Existing directory to enter, absolute or relative to the current directory. Omit to read the current directory."),
                }),
            Output = new ToolOutputDefinition(
                OutputSchema,
                (_, value) => [new TextBlock(value.GetProperty("cwd").GetString() ?? "")]),
            Execute = Execute,
        });
        return new CompositeDisposable(section, context, registration);
    }

    /** Runtime context after a change, so the model keeps the directory after the tool result is compacted away. */
    private static string ContextText(Session? session)
    {
        if (session?.WorkingDirectory is not { } cwd || cwd == session.Header.Cwd)
            return "";
        return session.Header.Cwd is { } root
            ? $"Current working directory: {cwd}. Write permission root: {root}; writes outside it need user approval."
            : $"Current working directory: {cwd}.";
    }

    private static Task<object?> Execute(JsonElement args, ToolRunContext exec)
    {
        var session = exec.Agent?.Session ?? throw new InvalidOperationException("working_directory requires an agent session");
        var cd = args.TryGetProperty("cd", out var cdElement) && cdElement.ValueKind == JsonValueKind.String
            ? cdElement.GetString()
            : null;
        if (cd is { Length: > 0 })
            return Task.FromResult<object?>(new WorkingDirectoryValue(SessionWorkingDirectory.Change(session, cd)));
        var current = session.WorkingDirectory ?? Environment.CurrentDirectory;
        // A directory removed after cd falls back to the original project, as long as that still exists.
        if (!Directory.Exists(current) && session.Header.Cwd is { } root)
            current = SessionWorkingDirectory.Change(session, root);
        return Task.FromResult<object?>(new WorkingDirectoryValue(current));
    }
}
