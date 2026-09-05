using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Goal;

public sealed record GoalToolsConfig
{
    public const long DefaultBlockedAfterConsecutiveRounds = 3;

    public long BlockedAfterConsecutiveRounds { get; init; } = DefaultBlockedAfterConsecutiveRounds;
}

public static class GoalTools
{
    public const string PluginName = "tool-goal";
    public const string InvalidUpdateCode = "GOAL_TOOL_INVALID_UPDATE";
    public const string BlockThresholdCode = "GOAL_TOOL_BLOCK_THRESHOLD";
    public const string InvalidArgsCode = "INVALID_ARGS";

    private const string CreateDescription =
        "Create one persisted same-session completion goal when the current direct human request "
        + "is a long-running objective that should continue across autonomous goal rounds. You may "
        + "infer that intent without requiring the user to say \"create a goal\". Do not use this for "
        + "trivial single-turn work. Execution rejects non-human and subagent authority.";

    private const string GetDescription =
        "Read the current same-session goal, including its exact id/revision, objective, phase, completed "
        + "continuation rounds, round limit, blocker reason when present, and whether another continuation is armed. "
        + "Call this before updating a goal.";

    private const string UpdateDescription =
        "Update the exact current goal revision. edit, pause, and resume require a direct "
        + "top-level human request. During an automatic continuation of the current goal, complete "
        + "and blocked are also allowed. blocked is rejected before the configured minimum round count; the model remains "
        + "responsible for judging that the same condition persisted across those rounds and must explain it in blocked_reason.";

    private static readonly IReadOnlySet<string> UpdateActions = new HashSet<string>
    {
        "edit", "pause", "resume", "complete", "blocked",
    };

    private static readonly JsonObject GoalOutputSchema = Schema("""
        {
          "oneOf": [
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["goal"],
              "properties": { "goal": { "type": "null" } }
            },
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["goal", "activation"],
              "properties": {
                "goal": {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["id", "revision", "objective", "phase", "roundsStarted", "maxGoalRounds"],
                  "properties": {
                    "id": { "type": "string" },
                    "revision": { "type": "integer" },
                    "objective": { "type": "string" },
                    "phase": { "type": "string", "enum": ["active", "paused", "blocked", "complete"] },
                    "roundsStarted": { "type": "integer" },
                    "maxGoalRounds": { "type": "integer" },
                    "blockedReason": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["code", "message"],
                      "properties": {
                        "code": { "type": "string" },
                        "message": { "type": "string" }
                      }
                    }
                  }
                },
                "activation": { "type": "string", "enum": ["armed", "disarmed"] }
              }
            }
          ]
        }
        """);

    private static readonly ToolOutputDefinition GoalOutput = new(
        GoalOutputSchema,
        (_, value) => [new TextBlock(value.GetRawText())]);

    public static IDisposable Apply(Context ctx, GoalToolsConfig? config = null)
    {
        var blockedAfter = ResolveConfig(config);
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)
            ?? throw new InvalidOperationException("tool-goal requires the systemPrompt service");
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)
            ?? throw new InvalidOperationException("tool-goal requires the tools service");
        return new RegistrationBundle(
            systemPrompt.Section(PromptSection.Literal("tool:goal", PromptOrders.ToolGoal, Guidance(blockedAfter))),
            tools.Register(GetGoalTool(ctx)),
            tools.Register(CreateGoalTool(ctx)),
            tools.Register(UpdateGoalTool(ctx, blockedAfter)));
    }

    private static long ResolveConfig(GoalToolsConfig? config)
    {
        var blockedAfter = config?.BlockedAfterConsecutiveRounds ?? GoalToolsConfig.DefaultBlockedAfterConsecutiveRounds;
        if (blockedAfter < 1)
            throw new ArgumentException("blockedAfterConsecutiveRounds must be a positive safe integer");
        return blockedAfter;
    }

    private static string Guidance(long blockedAfter)
        => "Use goal tools for one long-running completion objective in the current session. "
            + "create_goal may infer goal intent from a direct human request in any language; do not "
            + "create a goal for routine single-turn work. Call get_goal before update_goal and copy its "
            + "exact goal_id and revision. After session resume or fork, an active goal is disarmed: when "
            + "a human asks to continue or resume in any wording or language, use update_goal action "
            + "resume to rearm it. Mark complete only when the objective is actually achieved. Mark "
            + $"blocked only after the same blocking condition persists for at least {blockedAfter} "
            + "consecutive goal rounds, and report that concrete condition in blocked_reason; difficulty, uncertainty, "
            + "or useful remaining work is not blocked.";

    private static ToolDefinition GetGoalTool(Context ctx)
        => new()
        {
            Name = "get_goal",
            Description = GetDescription,
            Parameters = Schema("""{ "type": "object", "properties": {} }"""),
            Output = GoalOutput with { PresentationMeta = (_, _) => Present("Read current goal", "read", null) },
            Execute = (_, exec) =>
            {
                var execution = GoalAuthority.Execution(ctx, exec);
                return Task.FromResult<object?>(GoalValue(Goals(ctx).Get(execution.Agent)));
            },
        };

    private static ToolDefinition CreateGoalTool(Context ctx)
        => new()
        {
            Name = "create_goal",
            Description = CreateDescription,
            Parameters = Schema("""
                {
                  "type": "object",
                  "properties": {
                    "objective": {
                      "type": "string",
                      "description": "The concrete completion objective inferred from the direct human request."
                    },
                    "max_goal_rounds": {
                      "type": "number",
                      "description": "Optional positive safe-integer limit on automatic continuation rounds."
                    }
                  },
                  "required": ["objective"]
                }
                """),
            Output = GoalOutput with
            {
                PresentationMeta = (args, _) => StringArg(args, "objective") is { } objective
                    ? Present("Create goal", "other", CloneArg(args, "objective"))
                    : null,
            },
            Execute = (args, exec) =>
            {
                var execution = GoalAuthority.Execution(ctx, exec);
                GoalAuthority.RequireDirectHuman(ctx, execution);
                var rounds = NumberArg(args, "max_goal_rounds");
                var request = new CreateGoalRequest(
                    StringArg(args, "objective") ?? "",
                    rounds is null ? null : IntegralRounds(rounds.Value));
                return Task.FromResult<object?>(GoalValue(Goals(ctx).Create(execution.Agent, request)));
            },
        };

    private static ToolDefinition UpdateGoalTool(Context ctx, long blockedAfter)
        => new()
        {
            Name = "update_goal",
            Description = UpdateDescription,
            Parameters = Schema("""
                {
                  "type": "object",
                  "properties": {
                    "goal_id": { "type": "string", "description": "Exact id returned by get_goal." },
                    "revision": { "type": "number", "description": "Exact positive revision returned by get_goal." },
                    "action": {
                      "type": "string",
                      "enum": ["edit", "pause", "resume", "complete", "blocked"],
                      "description": "edit | pause | resume | complete | blocked"
                    },
                    "objective": { "type": "string", "description": "Replacement objective; valid only with action edit." },
                    "max_goal_rounds": { "type": "number", "description": "Replacement cap; valid only with action edit." },
                    "blocked_reason": { "type": "string", "description": "Concrete blocking condition; required only with action blocked." }
                  },
                  "required": ["goal_id", "revision", "action"]
                }
                """),
            Output = GoalOutput with { PresentationMeta = (args, _) => PresentUpdateCall(args) },
            Execute = (args, exec) => Task.FromResult<object?>(UpdateGoal(ctx, exec, args, blockedAfter)),
        };

    private static JsonElement UpdateGoal(Context ctx, ToolRunContext exec, JsonElement args, long blockedAfter)
    {
        var execution = GoalAuthority.Execution(ctx, exec);
        var goals = Goals(ctx);
        var reference = ResolveRef(StringArg(args, "goal_id") ?? "", NumberArg(args, "revision"));
        var objective = StringArg(args, "objective");
        var maxGoalRounds = NumberArg(args, "max_goal_rounds");
        var blockedReason = StringArg(args, "blocked_reason");
        var action = StringArg(args, "action");
        if (action is null || !UpdateActions.Contains(action))
        {
            throw new HarnessException(
                $"invalid arguments: \"action\" must be one of {string.Join(", ", UpdateActions.Order())}",
                InvalidArgsCode);
        }
        if (action == "edit")
        {
            GoalAuthority.RequireDirectHuman(ctx, execution);
            if (HasText(blockedReason))
                throw new HarnessException("blocked_reason is valid only with action blocked", InvalidUpdateCode);
            var editRequest = new EditGoalRequest(
                HasText(objective) ? objective : null,
                HasRoundCap(maxGoalRounds) ? IntegralRounds(maxGoalRounds!.Value) : null);
            return GoalValue(goals.Edit(execution.Agent, reference, editRequest));
        }
        if (action is "pause" or "resume")
        {
            GoalAuthority.RequireDirectHuman(ctx, execution);
            if (HasText(objective) || HasRoundCap(maxGoalRounds) || HasText(blockedReason))
            {
                throw new HarnessException(
                    "objective and max_goal_rounds are valid only with action edit; blocked_reason is valid only with action blocked",
                    InvalidUpdateCode);
            }
            var transitioned = action == "pause"
                ? goals.Pause(execution.Agent, reference)
                : goals.Resume(execution.Agent, reference);
            return GoalValue(transitioned);
        }
        var authority = GoalAuthority.Completion(ctx, execution);
        if (HasText(objective) || HasRoundCap(maxGoalRounds))
            throw new HarnessException("objective and max_goal_rounds are valid only with action edit", InvalidUpdateCode);
        if (action == "complete" && HasText(blockedReason))
            throw new HarnessException("blocked_reason is valid only with action blocked", InvalidUpdateCode);
        if (action == "blocked" && (blockedReason is null || blockedReason.Trim().Length == 0))
            throw new HarnessException("blocked_reason is required with action blocked", InvalidUpdateCode);
        if (action == "blocked" && authority is GoalToolAuthority.GoalRound round
            && round.Goal.RoundsStarted < blockedAfter)
        {
            throw new HarnessException(
                $"blocked requires at least {blockedAfter} consecutive goal rounds; current round is {round.Goal.RoundsStarted}",
                BlockThresholdCode);
        }
        var updated = action == "complete"
            ? goals.Complete(execution.Agent, reference)
            : goals.Block(execution.Agent, reference, new GoalBlockReason("model-reported", blockedReason!));
        if (authority is GoalToolAuthority.GoalRound)
        {
            exec.DeferContext(MessageFactory.CreateUserMessage(
                GoalWrapup.RenderContext(updated.Objective, action == "complete" ? null : blockedReason),
                new PluginMessageSource(
                    PluginName,
                    ContextForms.Notice,
                    Summary: MessageFactory.BoundContextSummary($"{action}: {updated.Objective}"))));
        }
        return GoalValue(updated);
    }

    private static GoalService Goals(Context ctx)
        => ctx.Get<GoalService>(GoalService.ServiceName)
            ?? throw new InvalidOperationException("tool-goal requires the goals service");

    private static GoalRef ResolveRef(string goalId, double? revision)
    {
        if (goalId.Length == 0 || goalId != goalId.Trim()
            || revision is not { } value || !double.IsInteger(value) || value < 1 || value > long.MaxValue)
        {
            throw new HarnessException(
                "goal_id must be non-empty and revision must be a positive safe integer",
                InvalidUpdateCode);
        }
        return new GoalRef(GoalId.Create(goalId), (long)value);
    }

    private static bool HasText(string? value) => value is not (null or "");

    private static bool HasRoundCap(double? value) => value is not null and not 0;

    private static long IntegralRounds(double value)
    {
        if (!double.IsInteger(value) || value > long.MaxValue)
            throw new GoalException("maxGoalRounds must be a positive safe integer", GoalErrorCodes.InvalidMaxRounds);
        return (long)value;
    }

    private static string? StringArg(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static double? NumberArg(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            ? element.GetDouble()
            : null;

    private static JsonElement GoalValue(GoalView? goal)
    {
        var root = new JsonObject();
        if (goal is null)
        {
            root["goal"] = null;
        }
        else
        {
            var goalNode = new JsonObject
            {
                ["id"] = goal.Id.Value,
                ["revision"] = goal.Revision,
                ["objective"] = goal.Objective,
                ["phase"] = GoalNames.Of(goal.Phase),
                ["roundsStarted"] = goal.RoundsStarted,
                ["maxGoalRounds"] = goal.MaxGoalRounds,
            };
            if (goal.BlockedReason is { } reason)
                goalNode["blockedReason"] = new JsonObject { ["code"] = reason.Code, ["message"] = reason.Message };
            root["goal"] = goalNode;
            root["activation"] = GoalNames.Of(goal.Activation);
        }
        return JsonDocument.Parse(root.ToJsonString()).RootElement;
    }

    private static JsonElement? PresentUpdateCall(JsonElement args)
    {
        if (!ValidUpdateArgs(args))
            return null;
        var action = args.GetProperty("action").GetString()!;
        var title = action == "blocked"
            ? "Mark goal"
            : $"{string.Concat(action[..1].ToUpperInvariant(), action.AsSpan(1))} goal";
        var rawInput = HasText(StringArg(args, "blocked_reason")) ? CloneArg(args, "blocked_reason")
            : HasText(StringArg(args, "objective")) ? CloneArg(args, "objective")
            : HasRoundCap(NumberArg(args, "max_goal_rounds")) ? CloneArg(args, "max_goal_rounds")
            : CloneArg(args, "goal_id");
        return Present(title, "other", rawInput);
    }

    private static bool ValidUpdateArgs(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return false;
        if (!args.TryGetProperty("goal_id", out var goalId) || goalId.ValueKind != JsonValueKind.String)
            return false;
        if (!args.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.Number)
            return false;
        if (!args.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String
            || action.GetString() is not { } actionName || !UpdateActions.Contains(actionName))
            return false;
        if (args.TryGetProperty("objective", out var objective) && objective.ValueKind != JsonValueKind.String)
            return false;
        if (args.TryGetProperty("max_goal_rounds", out var maxGoalRounds) && maxGoalRounds.ValueKind != JsonValueKind.Number)
            return false;
        return !args.TryGetProperty("blocked_reason", out var blockedReason) || blockedReason.ValueKind == JsonValueKind.String;
    }

    private static JsonNode CloneArg(JsonElement args, string name)
        => JsonNode.Parse(args.GetProperty(name).GetRawText())!;

    private static JsonElement Present(string title, string kind, JsonNode? rawInput)
    {
        var view = new JsonObject
        {
            ["card"] = "generic",
            ["title"] = title,
            ["kind"] = kind,
        };
        if (rawInput is not null)
            view["rawInput"] = rawInput;
        return JsonDocument.Parse(view.ToJsonString()).RootElement;
    }

    private static JsonObject Schema(string json) => JsonNode.Parse(json)!.AsObject();

    private sealed class RegistrationBundle(params IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}
