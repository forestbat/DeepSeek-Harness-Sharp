using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Subagent;

public static class SubagentControlTools
{
    public const string SendMessageToolName = "send_message";
    public const string InterruptAgentToolName = "interrupt_agent";
    public const string ListAgentsToolName = "list_agents";

    private const string SendMessageDescription =
        "Send a message to a live direct child subagent (continuable), or to the direct parent of this "
        + "session while running as a subagent. Plain user-role content; the target treats it as a new user message.";

    private const string InterruptDescription =
        "Interrupt a live direct child subagent (continuable). One-shot subagents and unknown ids are accepted as a no-op.";

    private const string ListAgentsDescription =
        "List the durable subagent identity of every session-backed subagent of this session (continuable and one-shot). "
        + "Use `scope: \"children\"` for direct children or `scope: \"descendants\"` for the whole subtree. "
        + "Corrupt rows are kept as diagnostic entries.";

    public static IDisposable Apply(Context ctx)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        return new DisposeBundle([tools.Register(SendMessageDefinition()), tools.Register(InterruptAgentDefinition())]);
    }

    public static IDisposable ApplyListAgents(Context ctx)
    {
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        return tools.Register(ListAgentsDefinition(ctx));
    }

    private static ToolDefinition SendMessageDefinition() => new()
    {
        Name = SendMessageToolName,
        Description = SendMessageDescription,
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("content", "target"),
            ["properties"] = new JsonObject
            {
                ["content"] = new JsonObject { ["type"] = "string", ["description"] = "Plain-text content delivered as one user message." },
                ["target"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("child", "parent"),
                    ["description"] = "Address a direct child subagent, or the direct parent while running as a subagent.",
                },
                ["childId"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Target child subagent id (from `subagent` with run_in_background, or `list_agents`). Required when target is `child`.",
                },
            },
        },
        Output = new ToolOutputDefinition(new JsonObject(), (_, _) => []),
        Execute = (_, _) => throw new SubagentException(
            "continuable subagents are not available in this deployment (the continuation runtime is not ported)",
            SubagentErrorCodes.ContinuationUnavailable),
    };

    private static ToolDefinition InterruptAgentDefinition() => new()
    {
        Name = InterruptAgentToolName,
        Description = InterruptDescription,
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("childId"),
            ["properties"] = new JsonObject
            {
                ["childId"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Child subagent id to interrupt (from `subagent` with run_in_background, or `list_agents`).",
                },
            },
        },
        Output = new ToolOutputDefinition(
            new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("accepted"),
                ["properties"] = new JsonObject
                {
                    ["accepted"] = new JsonObject { ["type"] = "boolean", ["enum"] = new JsonArray(true) },
                },
            },
            (args, _) => [new TextBlock($"Interrupt requested for subagent {args.GetProperty("childId").GetString()}")]),
        Execute = (args, exec) =>
        {
            // 一次性 in-process 子代不属于可寻址目标：与 TS 一致，接受为 no-op（run 的生命周期由其调用方的 signal 持有）。
            _ = exec.Agent
                ?? throw new InvalidOperationException("interrupt_agent requires a calling agent (exec.agent was undefined)");
            return Task.FromResult<object?>(new JsonObject { ["accepted"] = true });
        },
    };

    private static ToolDefinition ListAgentsDefinition(Context ctx) => new()
    {
        Name = ListAgentsToolName,
        Description = ListAgentsDescription,
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["scope"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("children", "descendants"),
                    ["description"] = "Enumeration scope: `children` lists direct children, `descendants` lists the entire subtree.",
                },
            },
        },
        Output = new ToolOutputDefinition(
            new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["oneOf"] = new JsonArray
                    (
                        new JsonObject
                        {
                            ["type"] = "object",
                            ["additionalProperties"] = false,
                            ["required"] = new JsonArray("kind", "id", "status"),
                            ["properties"] = new JsonObject
                            {
                                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("child") },
                                ["id"] = new JsonObject { ["type"] = "string" },
                                ["label"] = new JsonObject { ["type"] = "string" },
                                ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("running", "idle", "ready") },
                                ["parent"] = new JsonObject { ["type"] = "string" },
                                ["depth"] = new JsonObject { ["type"] = "integer" },
                            },
                        },
                        new JsonObject
                        {
                            ["type"] = "object",
                            ["additionalProperties"] = false,
                            ["required"] = new JsonArray("kind", "id", "reason"),
                            ["properties"] = new JsonObject
                            {
                                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("diagnostic") },
                                ["id"] = new JsonObject { ["type"] = "string" },
                                ["reason"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("corrupt", "unsupported", "unavailable") },
                                ["parent"] = new JsonObject { ["type"] = "string" },
                                ["depth"] = new JsonObject { ["type"] = "integer" },
                            },
                        }
                    ),
                },
            },
            (_, value) => RenderAgents(value)),
        Execute = (args, exec) => ListAgentsExecute(ctx, args, exec),
    };

    private static async Task<object?> ListAgentsExecute(Context ctx, JsonElement args, ToolRunContext exec)
    {
        var parent = exec.Agent
            ?? throw new InvalidOperationException("list_agents requires a calling agent (exec.agent was undefined)");
        var scope = args.TryGetProperty("scope", out var scopeValue) && scopeValue.ValueKind == JsonValueKind.String
            ? scopeValue.GetString() ?? "children"
            : "children";
        var runtime = ctx.Get<SubagentRuntime>(SubagentRuntime.ServiceName)!;
        var entries = scope == "children"
            ? await runtime.ListChildrenAsync(parent.Id, exec.Signal)
            : await runtime.ListDescendantsAsync(parent.Id, int.MaxValue, exec.Signal);
        var array = new JsonArray();
        foreach (var entry in entries)
            array.Add(Project(entry, runtime));
        return array;
    }

    private static JsonObject Project(SubagentListEntry entry, SubagentRuntime runtime)
    {
        var row = new JsonObject
        {
            ["id"] = entry.Id.Value,
        };
        if (entry.Parent is { } parent)
            row["parent"] = parent.Value;
        if (entry.Depth is { } depth)
            row["depth"] = depth;
        if (entry is SubagentListEntry.Child child)
        {
            row["kind"] = "child";
            if (child.Label is { } label)
                row["label"] = label;
            row["status"] = StatusOf(runtime, child.Id);
        }
        else if (entry is SubagentListEntry.Diagnostic diagnostic)
        {
            row["kind"] = "diagnostic";
            row["reason"] = diagnostic.Reason;
        }
        return row;
    }

    private static string StatusOf(SubagentRuntime runtime, SessionId id)
    {
        var agent = runtime.GetLive(id);
        return agent is null ? "ready" : agent.Status == AgentStatus.Running ? "running" : "idle";
    }

    private static IReadOnlyList<ContentBlock> RenderAgents(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
            return [new TextBlock("(no subagents)")];
        var descendants = value.EnumerateArray().Any(row => row.TryGetProperty("depth", out _));
        var lines = new List<string>();
        foreach (var row in value.EnumerateArray())
        {
            var at = descendants
                ? $" parent={row.GetProperty("parent").GetString()} depth={row.GetProperty("depth").GetInt32()}"
                : "";
            lines.Add(row.GetProperty("kind").GetString() == "child"
                ? $"{row.GetProperty("id").GetString()} [{row.GetProperty("status").GetString()}]{at} — {(row.TryGetProperty("label", out var label) ? label.GetString() : "")}"
                : $"{row.GetProperty("id").GetString()} [diagnostic: {row.GetProperty("reason").GetString()}]{at}");
        }
        return [new TextBlock(string.Join("\n", lines))];
    }

    private sealed class DisposeBundle(IReadOnlyList<IDisposable> disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}
