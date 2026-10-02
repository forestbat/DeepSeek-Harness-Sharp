using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Compaction;

public sealed record CompactToolResult(bool Compacted, int ShadowedItems, int ShadowedTokens);

/** compact:模型主动压缩较旧对话历史,可选 focus 引导摘要侧重;与 /compact 共用 tail_turns+预算选区。 */
public static class CompactTool
{
    public const string ToolName = "compact";

    public const string SectionText =
        "compact condenses older conversation history when context is getting tight. "
        + "It retains the most recent turns and replaces earlier history with a checkpoint summary, then the turn continues. "
        + "Pass an optional focus to steer what the summary must keep, for example \"the failing test and the API contract we agreed on\".";

    public static IDisposable Register(Context ctx)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var section = systemPrompt.Section(PromptSection.Literal("tool:compact", SectionText));
        var registration = tools.Register(new ToolDefinition
        {
            Name = ToolName,
            Description = "Condense older conversation history into a checkpoint summary while keeping the most recent turns.",
            Parameters = JsonNode.Parse("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "properties": {
                    "focus": {
                      "type": "string",
                      "description": "Optional note describing what the summary must preserve or emphasize."
                    }
                  }
                }
                """)!.AsObject(),
            Output = new ToolOutputDefinition(
                JsonNode.Parse("""
                    {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["compacted", "shadowedItems", "shadowedTokens"],
                      "properties": {
                        "compacted": { "type": "boolean" },
                        "shadowedItems": { "type": "integer" },
                        "shadowedTokens": { "type": "integer" }
                      }
                    }
                    """)!.AsObject(),
                (_, value) => value.GetProperty("compacted").GetBoolean()
                    ? [new TextBlock($"Compacted {value.GetProperty("shadowedItems").GetInt32()} history items (~{value.GetProperty("shadowedTokens").GetInt32()} tokens).")]
                    : [new TextBlock("No compactable history yet.")]),
            Execute = (args, exec) => Execute(ctx, args, exec),
        });
        return new Registration(registration, section);
    }

    private static async Task<object?> Execute(Context ctx, JsonElement args, ToolRunContext exec)
    {
        var agent = exec.Agent ?? throw new InvalidOperationException("compact requires the calling agent");
        var focus = args.TryGetProperty("focus", out var focusElement) && focusElement.ValueKind == JsonValueKind.String
            ? focusElement.GetString()
            : null;
        var compaction = ctx.Get<CompactionEngine>(CompactionEngine.ServiceName)!;
        var result = await compaction.CompactInTurn(agent, focus, exec.Signal);
        return result is null
            ? new CompactToolResult(false, 0, 0)
            : new CompactToolResult(true, result.ShadowedSeqs.Count, result.ShadowedTokenCount);
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
