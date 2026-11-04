using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Ide.History;

public static class IdeHistoryTool
{
    public const string ToolName = "ide_history";
    public const int DefaultLimit = 20;
    public const int MaxContentChars = 20000;
    private const long MaxContentBytes = 2 * 1024 * 1024;

    public static IDisposable Register(Context ctx, IdeHistoryRegistry registry)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)
            ?? throw new InvalidOperationException("ide-history requires the toolRuntime service");
        return tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = "Read IDE local history (VSCode Timeline history and JetBrains Local History) for a file that is not recoverable from git. "
                + "Use action=list to find historical versions of a path and action=read with the entry timestamp to fetch the content. Read-only.",
            Parameters = Schema("""
                {
                  "type": "object",
                  "properties": {
                    "action": { "type": "string", "enum": ["list", "read", "stores"] },
                    "path": { "type": "string", "description": "Absolute file path to filter by (list/read)." },
                    "timestamp": { "type": "number", "description": "Entry timestamp in epoch milliseconds, from a list result (read)." },
                    "provider": { "type": "string", "description": "Optional provider filter: vscode | jetbrains." },
                    "limit": { "type": "number", "description": "Maximum entries to return (list, default 20)." }
                  },
                  "required": ["action"]
                }
                """),
            Output = new ToolOutputDefinition(
                Schema("""{ "type": "object" }"""),
                (_, value) => [new TextBlock(value.ValueKind is System.Text.Json.JsonValueKind.Undefined ? "{}" : value.GetRawText())]),
            Execute = (args, _) => Task.FromResult<object?>(Execute(registry, args)),
        });
    }

    private static JsonObject Execute(IdeHistoryRegistry registry, System.Text.Json.JsonElement args)
    {
        var action = StringArg(args, "action") ?? "list";
        var path = StringArg(args, "path");
        var provider = StringArg(args, "provider");
        var limit = (int)Math.Clamp(NumberArg(args, "limit") ?? DefaultLimit, 1, 200);
        switch (action)
        {
            case "stores":
                var stores = new JsonArray();
                foreach (var note in registry.RetentionNotes())
                    stores.Add(note);
                return new JsonObject { ["stores"] = stores, ["note"] = "两 IDE 共存时按每条记录的实际时间戳取较长保留期：先选中在指定路径上仍保有更早版本的一方。" };
            case "list":
                if (path is not { Length: > 0 })
                    return Error("path is required for action=list");
                var entries = new JsonArray();
                foreach (var (providerInstance, _, entry) in registry.Query(path, provider, limit))
                {
                    entries.Add(new JsonObject
                    {
                        ["provider"] = providerInstance.Name,
                        ["path"] = entry.Path,
                        ["timestamp"] = entry.Timestamp,
                        ["time"] = entry.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                        ["kind"] = entry.Kind,
                        ["hasContent"] = entry.HasContent,
                    });
                }
                return new JsonObject { ["entries"] = entries, ["count"] = entries.Count };
            case "read":
                if (path is not { Length: > 0 })
                    return Error("path is required for action=read");
                if (NumberArg(args, "timestamp") is not { } timestamp)
                    return Error("timestamp is required for action=read");
                var match = registry.Find(path, (long)timestamp, provider);
                if (match is null)
                    return Error($"no history entry at timestamp {timestamp} for {path}");
                var (matchProvider, store, matchEntry) = match.Value;
                var content = matchProvider.Read(store, matchEntry);
                if (content is null)
                {
                    return new JsonObject
                    {
                        ["provider"] = matchProvider.Name,
                        ["path"] = matchEntry.Path,
                        ["timestamp"] = matchEntry.Timestamp,
                        ["kind"] = matchEntry.Kind,
                        ["contentUnavailable"] = matchProvider.LastReadError ?? "history entry content is missing on disk",
                    };
                }
                if (content.Bytes.LongLength > MaxContentBytes)
                    return Error($"history entry is too large: {content.Bytes.LongLength} bytes (limit {MaxContentBytes})");
                var text = content.Text;
                var truncated = text.Length > MaxContentChars;
                return new JsonObject
                {
                    ["provider"] = matchProvider.Name,
                    ["path"] = matchEntry.Path,
                    ["timestamp"] = matchEntry.Timestamp,
                    ["time"] = matchEntry.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    ["kind"] = matchEntry.Kind,
                    ["origin"] = content.Origin,
                    ["truncated"] = truncated,
                    ["content"] = truncated ? text[..MaxContentChars] : text,
                };
            default:
                return Error($"unknown action: {action}");
        }
    }

    private static JsonObject Error(string message) => new() { ["error"] = message };

    private static JsonObject Schema(string json) => JsonNode.Parse(json)!.AsObject();

    private static string? StringArg(System.Text.Json.JsonElement args, string name)
        => args.ValueKind == System.Text.Json.JsonValueKind.Object
            && args.TryGetProperty(name, out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? NumberArg(System.Text.Json.JsonElement args, string name)
        => args.ValueKind == System.Text.Json.JsonValueKind.Object
            && args.TryGetProperty(name, out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
