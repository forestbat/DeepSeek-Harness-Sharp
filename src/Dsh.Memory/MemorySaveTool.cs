using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Memory;

public sealed record MemorySaveResult(string Action, string Key, string Section, bool Replaced);

/** memory_save:项目记忆的记录级操作(remember/correct/forget/skip);记录带时间戳,按 `## 节` 分组。 */
public static class MemorySaveTool
{
    public const string ToolName = "memory_save";

    private const string SectionText =
        "Project memory holds stable project facts, decisions, constraints, environment commands and corrections. "
        + "Use memory_save with action remember (upsert a record under a `## section`), correct (record a correction), "
        + "forget (remove a record by key), or skip (decline out-of-scope content). "
        + "Records are timestamped automatically; the injected index is capped at 8192 bytes, so read the memory file directly when you need full content.";

    public static IDisposable Register(Context ctx, ProjectMemory memory)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal("tool:memory_save", SectionText));
        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = "Record-level operations on the project memory: remember, correct, forget, or skip.",
            Parameters = JsonNode.Parse("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["action"],
                  "properties": {
                    "action": {
                      "type": "string",
                      "enum": ["remember", "correct", "forget", "skip"],
                      "description": "remember: upsert a record; correct: record a correction; forget: remove a record by key; skip: decline out-of-scope content."
                    },
                    "key": { "type": "string", "description": "Record key, slug-like, e.g. \"build.command\". Required for remember/correct/forget." },
                    "text": { "type": "string", "description": "One-line record body. Required for remember/correct." },
                    "section": { "type": "string", "description": "Target section for remember, e.g. \"Facts\", \"Decisions\", \"Constraints\", \"Commands\". Defaults to \"Facts\"." },
                    "reason": { "type": "string", "description": "Why the content is out of scope (for skip)." }
                  }
                }
                """)!.AsObject(),
            Output = new ToolOutputDefinition(
                JsonNode.Parse("""
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["action", "key", "section", "replaced"],
                      "properties": {
                        "action": { "type": "string" },
                        "key": { "type": "string" },
                        "section": { "type": "string" },
                        "replaced": { "type": "boolean" }
                      }
                    }
                    """)!.AsObject(),
                (_, value) =>
                [
                    new TextBlock(
                        $"memory {value.GetProperty("action").GetString()}: {value.GetProperty("key").GetString()}"
                        + $" (section: {value.GetProperty("section").GetString()}, replaced: {value.GetProperty("replaced").GetBoolean()})"),
                ]),
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
        var key = args.TryGetProperty("key", out var keyElement) ? keyElement.GetString() : null;
        var text = args.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
        switch (action)
        {
            case "remember":
                Require(key, "key", action);
                Require(text, "text", action);
                var section = args.TryGetProperty("section", out var sectionElement) ? sectionElement.GetString() : null;
                return ToResult(await memory.RememberAsync(key!, text!, section, "tool", exec.Signal));
            case "correct":
                Require(key, "key", action);
                Require(text, "text", action);
                return ToResult(await memory.CorrectAsync(key!, text!, "tool", exec.Signal));
            case "forget":
                Require(key, "key", action);
                return ToResult(await memory.ForgetAsync(key!, "tool", exec.Signal));
            case "skip":
                var reason = args.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() : null;
                return new MemorySaveResult("skip", key ?? "", string.IsNullOrWhiteSpace(reason) ? "out_of_scope" : reason, false);
            default:
                throw new InvalidOperationException($"memory_save: unknown action \"{action}\" (expected remember|correct|forget|skip)");
        }
    }

    private static MemorySaveResult ToResult(MemoryOpResult op)
        => new(op.Action, op.Key, op.Section, op.Replaced);

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
