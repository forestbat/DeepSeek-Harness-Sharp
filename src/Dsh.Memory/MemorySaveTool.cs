using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Memory;

public sealed record MemorySaveResult(
    string Action,
    IReadOnlyList<string> Keys,
    IReadOnlyList<string> Sections,
    int Count,
    bool Replaced,
    bool DryRun,
    string? Reason = null);

/** memory_save:项目记忆的记录级操作(remember/correct/forget/skip);记录带时间戳,按 `## 节` 分组。 */
public static class MemorySaveTool
{
    public const string ToolName = "memory_save";

    private const string SectionText =
        "Project memory holds stable project facts, decisions, constraints, environment commands and corrections. "
        + "Use memory_save with action remember (upsert a record under a `## section`), correct (record a correction), "
        + "forget (remove records by key(s), or by a text query, optionally scoped to a section; dry_run previews the match), "
        + "or skip (decline out-of-scope content). "
        + "Records are timestamped automatically; the injected index is capped at 8192 bytes, so read the memory file directly when you need full content.";

    public static IDisposable Register(Context ctx, ProjectMemory memory)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal("tool:memory_save", SectionText));
        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = "Record-level operations on the project memory: remember, correct, forget (by key(s) or text query), or skip.",
            Parameters = JsonNode.Parse("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["action"],
                  "properties": {
                    "action": {
                      "type": "string",
                      "enum": ["remember", "correct", "forget", "skip"],
                      "description": "remember: upsert a record; correct: record a correction; forget: remove records by key(s) or text query; skip: decline out-of-scope content."
                    },
                    "key": { "type": "string", "description": "Record key, slug-like, e.g. \"build.command\". Required for remember/correct; single-key forget." },
                    "keys": { "type": "array", "items": { "type": "string" }, "description": "Forget only: keys to remove (case-insensitive). Use instead of key/query." },
                    "query": { "type": "string", "description": "Forget only: remove records whose text contains this (case-insensitive, minimum 2 characters). Use instead of key/keys." },
                    "text": { "type": "string", "description": "One-line record body. Required for remember/correct." },
                    "section": { "type": "string", "description": "Target section for remember, e.g. \"Facts\", \"Decisions\", \"Constraints\", \"Commands\". Defaults to \"Facts\"; optional section filter for forget." },
                    "dry_run": { "type": "boolean", "description": "Forget only: report matching records without removing them." },
                    "reason": { "type": "string", "description": "Why the content is out of scope (for skip)." }
                  }
                }
                """)!.AsObject(),
            Output = new ToolOutputDefinition(
                JsonNode.Parse("""
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["action", "count", "keys", "sections", "dryRun"],
                      "properties": {
                        "action": { "type": "string" },
                        "count": { "type": "integer" },
                        "keys": { "type": "array", "items": { "type": "string" } },
                        "sections": { "type": "array", "items": { "type": "string" } },
                        "replaced": { "type": "boolean" },
                        "dryRun": { "type": "boolean" },
                        "reason": { "type": "string" }
                      }
                    }
                    """)!.AsObject(),
                (_, value) => RenderResult(value)),
            Execute = (args, exec) => Execute(args, exec, memory),
        });
        return new Registration(registration, section);
    }

    private static async Task<object?> Execute(
        JsonElement args,
        ToolRunContext exec,
        ProjectMemory memory)
    {
        var action = args.TryGetProperty("action", out var actionElement) ? actionElement.GetString() : null;
        var key = GetString(args, "key");
        var text = GetString(args, "text");
        var section = GetString(args, "section");
        var query = GetString(args, "query");
        var dryRun = args.TryGetProperty("dry_run", out var dryElement) && dryElement.ValueKind == JsonValueKind.True;
        var keys = ReadStringArray(args, "keys");
        switch (action)
        {
            case "remember":
                Require(key, "key", action);
                Require(text, "text", action);
                var remembered = await memory.RememberAsync(key!, text!, section, "tool", exec.Signal);
                return new MemorySaveResult("remember", [remembered.Key], [remembered.Section], 1, remembered.Replaced, false);
            case "correct":
                Require(key, "key", action);
                Require(text, "text", action);
                var corrected = await memory.CorrectAsync(key!, text!, "tool", exec.Signal);
                return new MemorySaveResult("correct", [corrected.Key], [corrected.Section], 1, corrected.Replaced, false);
            case "forget":
                var forgotten = await memory.ForgetAsync(BuildForgetRequest(key, keys, query, section, dryRun), "tool", exec.Signal);
                return new MemorySaveResult("forget", forgotten.Keys, forgotten.Sections, forgotten.Count, false, forgotten.DryRun);
            case "skip":
                var reason = GetString(args, "reason");
                return new MemorySaveResult("skip", [], [], 0, false, false, string.IsNullOrWhiteSpace(reason) ? "out_of_scope" : reason);
            default:
                throw new InvalidOperationException($"memory_save: unknown action \"{action}\" (expected remember|correct|forget|skip)");
        }
    }

    private static MemoryForgetRequest BuildForgetRequest(string? key, IReadOnlyList<string> keys, string? query, string? section, bool dryRun)
    {
        var selectors = (string.IsNullOrWhiteSpace(key) ? 0 : 1)
            + (keys.Count > 0 ? 1 : 0)
            + (string.IsNullOrWhiteSpace(query) ? 0 : 1);
        if (selectors == 0)
            throw new InvalidOperationException("memory_save: action \"forget\" requires key, keys, or query");
        if (selectors > 1)
            throw new InvalidOperationException("memory_save: action \"forget\" accepts only one of key, keys, or query");
        var allKeys = new List<string>();
        if (!string.IsNullOrWhiteSpace(key))
            allKeys.Add(key);
        allKeys.AddRange(keys);
        return new MemoryForgetRequest(allKeys.Count > 0 ? allKeys : null, query, section, dryRun);
    }

    private static IReadOnlyList<ContentBlock> RenderResult(JsonElement value)
    {
        var action = value.GetProperty("action").GetString() ?? "";
        if (action == "skip")
            return [new TextBlock($"memory skip: {value.GetProperty("reason").GetString()}")];
        var keys = value.GetProperty("keys").EnumerateArray()
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrEmpty(item))
            .ToList();
        if (action == "forget")
        {
            var count = value.GetProperty("count").GetInt32();
            var joined = string.Join(", ", keys);
            return value.GetProperty("dryRun").GetBoolean()
                ? [new TextBlock($"memory forget (dry-run): would remove {count} record(s): {joined}")]
                : [new TextBlock($"memory forget: removed {count} record(s): {joined}")];
        }
        var section = value.GetProperty("sections").EnumerateArray().Select(item => item.GetString()).FirstOrDefault() ?? "";
        return [new TextBlock($"memory {action}: {keys.FirstOrDefault()} (section: {section}, replaced: {value.GetProperty("replaced").GetBoolean()})")];
    }

    private static string? GetString(JsonElement args, string name)
        => args.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static List<string> ReadStringArray(JsonElement args, string name)
        => !args.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array
            ? []
            : [.. element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .Where(value => !string.IsNullOrWhiteSpace(value))];

    private static void Require(string? value, string name, string action)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"memory_save: action \"{action}\" requires a non-empty \"{name}\"");
    }

    private sealed class Registration(IDisposable registration, IDisposable section) : IDisposable
    {
        public void Dispose()
        {
            section.Dispose();
            registration.Dispose();
        }
    }
}
