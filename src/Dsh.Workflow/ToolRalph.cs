using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Subagent;

namespace Dsh.Workflow;

public sealed record ToolRalphConfig
{
    public string SubagentProvider { get; init; } = "spawn";
    public long MaxRounds { get; init; } = 256;
    public long MaxHandoffChars { get; init; } = 16_384;
    public long MaxResultChars { get; init; } = 16_384;
}

public static class ToolRalph
{
    private const string Description =
        "Run a foreground fresh-agent Ralph loop toward one immutable objective. "
        + "Use only when the direct human explicitly asks for Ralph or fresh-agent iteration. Each round "
        + "opens a new child with no parent conversation or prior child session; the shared workspace is "
        + "long-term memory, and only a bounded structured report crosses rounds. The call returns when "
        + "a worker reports completion or a concrete blocker, or at the round limit. Ordinary long-running same-session work "
        + "belongs to goal tools.";

    private const string RalphMetaName = "ralph-loop";
    private const string RalphMetaDescription = "Iterate toward one objective with a fresh child and bounded structured handoff per round.";
    private const string RalphMetaPhaseTitle = "Fresh-agent rounds";
    private const string RalphMetaPhaseDetail = "One clean child context per Ralph round.";
    private const string TruncationNotice = "\n… [truncated]";

    private static readonly Dictionary<string, object?> RalphMeta = new()
    {
        ["name"] = RalphMetaName,
        ["description"] = RalphMetaDescription,
        ["phases"] = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["title"] = RalphMetaPhaseTitle,
                ["detail"] = RalphMetaPhaseDetail,
            },
        },
    };

    private const string RalphScript = """
        const reportSchema = {
          type: 'object',
          properties: {
            status: { type: 'string', enum: ['continue', 'complete', 'blocked'] },
            summary: { type: 'string' },
            evidence: { type: 'array', items: { type: 'string' } },
            nextSteps: { type: 'array', items: { type: 'string' } },
            blocker: { type: 'string' },
          },
          required: ['status', 'summary', 'evidence', 'nextSteps', 'blocker'],
          additionalProperties: false,
        }

        function normalizedText(value) {
          return typeof value === 'string' && value.length > 0 && value === value.trim()
        }

        function normalizedList(value) {
          return Array.isArray(value) && value.every(normalizedText)
        }

        function validateReport(report) {
          if (report === null || typeof report !== 'object' || Array.isArray(report)) {
            throw new Error('Ralph child returned no structured round report')
          }
          if (!normalizedText(report.summary)) {
            throw new Error('Ralph round report summary must be non-empty and normalized')
          }
          if (!normalizedList(report.evidence) || !normalizedList(report.nextSteps)) {
            throw new Error('Ralph round report evidence and nextSteps must contain only non-empty normalized strings')
          }
          if (typeof report.blocker !== 'string' || report.blocker !== report.blocker.trim()) {
            throw new Error('Ralph round report blocker must be a normalized string')
          }
          switch (report.status) {
            case 'continue':
              if (report.nextSteps.length === 0 || report.blocker !== '') {
                throw new Error('a continuing Ralph report needs nextSteps and an empty blocker')
              }
              break
            case 'complete':
              if (report.evidence.length === 0 || report.nextSteps.length !== 0 || report.blocker !== '') {
                throw new Error('a complete Ralph report needs evidence, no nextSteps, and an empty blocker')
              }
              break
            case 'blocked':
              if (!normalizedText(report.blocker)) {
                throw new Error('a blocked Ralph report needs a concrete blocker')
              }
              break
            default:
              throw new Error('Ralph round report status is invalid')
          }
          const serialized = JSON.stringify(report)
          if (serialized.length > args.maxHandoffChars) {
            throw new Error('Ralph round report exceeds maxHandoffChars (' + serialized.length + ' > ' + args.maxHandoffChars + ')')
          }
          return report
        }

        let previous
        phase('Fresh-agent rounds')
        for (let round = 1; round <= args.maxRounds; round += 1) {
          const prior = previous === undefined ? '(none — this is the first round)' : JSON.stringify(previous)
          const prompt = [
            'You are one fresh worker in a foreground Ralph loop. You receive no parent conversation and no prior child session. Do not call the ralph tool: this round already is its worker.',
            'Immutable objective:\n' + args.objective,
            'Ralph round: ' + round + ' of ' + args.maxRounds + '.',
            'The shared workspace and its current working tree are the long-term memory and source of truth. Inspect them before acting, preserve existing work, perform concrete in-scope work, and verify what you change. Treat the previous report only as a bounded handoff; confirm it against the workspace.',
            'Previous structured handoff:\n' + prior,
            'Return one report with exact normalized strings. Use status continue with at least one nextSteps entry while useful work remains; complete only with concrete evidence and no nextSteps; blocked only when no meaningful progress is possible without human input or an external-state change. blocker must be empty unless blocked.',
          ].join('\n\n')
          const rawReport = await agent(prompt, {
            label: 'Ralph round ' + round,
            phase: 'Fresh-agent rounds',
            schema: reportSchema,
          })
          if (rawReport === null) {
            return { status: 'round-failed', roundsStarted: round, lastReport: previous ?? null }
          }
          const report = validateReport(rawReport)
          if (report.status === 'complete') return { status: 'complete', roundsStarted: round, report }
          if (report.status === 'blocked') return { status: 'blocked', roundsStarted: round, report }
          previous = report
        }
        return { status: 'budget-limited', roundsStarted: args.maxRounds, report: previous }
        """;

    public static IDisposable Apply(Context ctx, object? config)
    {
        var resolved = ResolveConfig(config);
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var workflow = ctx.Get<WorkflowEngine>(WorkflowEngine.ServiceName)!;
        var subagents = ctx.Get<SubagentRuntime>(SubagentRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var prompt = systemPrompt.Section(PromptSection.Literal(
            "tool:ralph",
            PromptOrders.ToolRalph,
            "Use the ralph tool ONLY when the direct human explicitly asks for a Ralph loop or fresh-agent iterative execution. Each Ralph round starts a fresh child with no conversation seed and uses the shared workspace as durable memory. Completion and blockers are worker reports, not independent evaluation. Use same-session goal tools for ordinary long-running objectives, and plain subagents or workflows for bounded delegation and fan-out."));
        var registration = tools.Register(BuildDefinition(workflow, subagents, resolved));
        return new DisposeBundle([prompt, registration]);
    }

    public static IDisposable Apply(Context ctx, ToolRalphConfig config) => Apply(ctx, (object?)config);

    private static ToolRalphConfig ResolveConfig(object? config)
    {
        if (config is ToolRalphConfig typed)
            return typed;
        var dict = config as IReadOnlyDictionary<string, object?>;
        var resolved = new ToolRalphConfig
        {
            SubagentProvider = dict?.GetValueOrDefault("subagentProvider") as string ?? "spawn",
            MaxRounds = LongOf(dict, "maxRounds") ?? 256,
            MaxHandoffChars = LongOf(dict, "maxHandoffChars") ?? 16_384,
            MaxResultChars = LongOf(dict, "maxResultChars") ?? 16_384,
        };
        if (resolved.SubagentProvider.Length == 0 || resolved.SubagentProvider != resolved.SubagentProvider.Trim())
            throw new ArgumentException("subagentProvider must be a non-empty normalized string");
        if (resolved.MaxRounds < 1)
            throw new ArgumentException("maxRounds must be a positive safe integer");
        if (resolved.MaxHandoffChars < 1)
            throw new ArgumentException("maxHandoffChars must be a positive safe integer");
        if (resolved.MaxResultChars < 1)
            throw new ArgumentException("maxResultChars must be a positive safe integer");
        return resolved;
    }

    private static ToolDefinition BuildDefinition(WorkflowEngine workflow, SubagentRuntime subagents, ToolRalphConfig config)
    {
        return new ToolDefinition
        {
            Name = "ralph",
            Description = Description,
            Parameters = ParameterSchema(),
            Output = new ToolOutputDefinition(
                new JsonObject(),
                (args, value) => [new TextBlock(RenderValue(value, config.MaxResultChars))],
                (args, _) => PresentCall(args)),
            Execute = (args, exec) => ExecuteAsync(workflow, subagents, config, args, exec),
        };
    }

    private static async Task<object?> ExecuteAsync(
        WorkflowEngine workflow,
        SubagentRuntime subagents,
        ToolRalphConfig config,
        JsonElement args,
        ToolRunContext exec)
    {
        var parent = exec.Agent
            ?? throw new InvalidOperationException("Ralph tool requires a calling agent (exec.agent was undefined)");
        var objective = (args.TryGetProperty("objective", out var objectiveElement)
            ? objectiveElement.GetString() ?? ""
            : "").Trim();
        if (objective.Length == 0)
            throw new InvalidOperationException("Ralph objective must be a non-empty string");
        var maxRounds = ResolveMaxRounds(
            args.TryGetProperty("maxRounds", out var maxRoundsElement) && maxRoundsElement.ValueKind == JsonValueKind.Number
                ? maxRoundsElement.GetInt64()
                : null,
            config.MaxRounds);
        RequireFreshProvider(subagents, config.SubagentProvider);

        var run = workflow.Start(new WorkflowStartRequest
        {
            Script = RalphScript,
            Meta = RalphMeta,
            Args = new Dictionary<string, object?>
            {
                ["objective"] = objective,
                ["maxRounds"] = maxRounds,
                ["maxHandoffChars"] = config.MaxHandoffChars,
            },
            SubagentProvider = config.SubagentProvider,
            MaxTotalAgents = checked((int)maxRounds),
            Parent = parent,
            Signal = exec.Signal,
        });
        var abortRegistration = exec.Signal.Register(() => run.Cancel("parent step aborted"));
        if (exec.Signal.IsCancellationRequested)
            run.Cancel("parent step aborted");

        try
        {
            var settled = await run.Result;
            var error = StopReasonError(settled);
            if (error is not null)
                throw new InvalidOperationException(error);
            var value = ReadRunResult(settled.Value, maxRounds, config.MaxHandoffChars);
            if (value.TryGetValue("status", out var statusValue) && statusValue as string == "round-failed")
                throw new InvalidOperationException(RenderRoundFailure(value, config.MaxResultChars));
            return new
            {
                runId = run.Id.Value,
                agentsStarted = settled.AgentsStarted,
                result = value,
            };
        }
        finally
        {
            abortRegistration.Dispose();
            await run.DisposeAsync();
        }
    }

    private static long ResolveMaxRounds(long? requested, long ceiling)
    {
        var value = requested ?? ceiling;
        if (value < 1)
            throw new ArgumentException("Ralph maxRounds must be a positive safe integer");
        if (value > ceiling)
            throw new ArgumentException($"Ralph maxRounds {value} exceeds the deployment ceiling {ceiling}");
        return value;
    }

    private static void RequireFreshProvider(SubagentRuntime subagents, string providerName)
    {
        var provider = subagents.GetProvider(providerName)
            ?? throw new InvalidOperationException($"Ralph subagent provider \"{providerName}\" is not registered");
        if (!provider.Capabilities.OutputSchema)
            throw new InvalidOperationException($"Ralph subagent provider \"{providerName}\" does not support structured output");
        if (provider.InheritsParentContext)
            throw new InvalidOperationException($"Ralph subagent provider \"{providerName}\" inherits parent context; Ralph requires a fresh provider");
    }

    private static bool IsRecord(object? value)
        => value is IDictionary<string, object?> && value is not string;

    private static bool NormalizedText(object? value)
        => value is string text && text.Length > 0 && text == text.Trim();

    private static bool NormalizedList(object? value)
        => value is IEnumerable<object?> items && items.All(NormalizedText);

    private static int CountOf(object? value)
        => value is IEnumerable<object?> items ? items.Count() : 0;

    private static Dictionary<string, object?> ReadReport(object? value, string expectedStatus, long maxChars)
    {
        if (!IsRecord(value)
            || value is not IDictionary<string, object?> record
            || string.Join(',', record.Keys.Order(StringComparer.Ordinal)) != "blocker,evidence,nextSteps,status,summary"
            || ValueOf(record, "status") as string != expectedStatus
            || !NormalizedText(ValueOf(record, "summary"))
            || !NormalizedList(ValueOf(record, "evidence"))
            || !NormalizedList(ValueOf(record, "nextSteps"))
            || ValueOf(record, "blocker") is not string blocker
            || blocker != blocker.Trim())
        {
            throw new InvalidOperationException("Ralph workflow returned a malformed round report");
        }

        var report = new Dictionary<string, object?>
        {
            ["status"] = expectedStatus,
            ["summary"] = ValueOf(record, "summary")!,
            ["evidence"] = ValueOf(record, "evidence")!,
            ["nextSteps"] = ValueOf(record, "nextSteps")!,
            ["blocker"] = blocker,
        };
        if (expectedStatus == "continue" && (CountOf(report["nextSteps"]) == 0 || report["blocker"] as string != ""))
            throw new InvalidOperationException("Ralph workflow returned an invalid continuing report");
        if (expectedStatus == "complete"
            && (CountOf(report["evidence"]) == 0
                || CountOf(report["nextSteps"]) != 0
                || report["blocker"] as string != ""))
        {
            throw new InvalidOperationException("Ralph workflow returned an invalid completion report");
        }

        if (expectedStatus == "blocked" && !NormalizedText(report["blocker"]))
            throw new InvalidOperationException("Ralph workflow returned an invalid blocked report");
        var chars = JsonSerializer.Serialize(report).Length;
        if (chars > maxChars)
            throw new InvalidOperationException($"Ralph workflow returned an oversized handoff ({chars} > {maxChars})");
        return report;
    }

    private static Dictionary<string, object?> ReadRunResult(object? value, long maxRounds, long maxHandoffChars)
    {
        if (!IsRecord(value) || value is not IDictionary<string, object?> record
            || ValueOf(record, "roundsStarted") is not double roundsStarted
            || roundsStarted != Math.Floor(roundsStarted)
            || roundsStarted < 1
            || roundsStarted > maxRounds)
        {
            throw new InvalidOperationException("Ralph workflow returned a malformed terminal result");
        }

        var roundsStartedInt = checked((int)roundsStarted);
        var status = ValueOf(record, "status") as string;
        switch (status)
        {
            case "complete":
            case "blocked":
            case "budget-limited":
                if (string.Join(',', record.Keys.Order(StringComparer.Ordinal)) != "report,roundsStarted,status")
                    throw new InvalidOperationException("Ralph workflow returned a malformed terminal result");
                if (status == "budget-limited" && maxRounds != long.MaxValue && roundsStarted != maxRounds)
                    throw new InvalidOperationException("Ralph workflow returned budget-limited before the round limit");
                var terminalReport = ReadReport(ValueOf(record, "report"), status == "budget-limited" ? "continue" : status, maxHandoffChars);
                return new Dictionary<string, object?>
                {
                    ["status"] = status,
                    ["roundsStarted"] = roundsStartedInt,
                    ["report"] = terminalReport,
                };
            case "round-failed":
            {
                if (string.Join(',', record.Keys.Order(StringComparer.Ordinal)) != "lastReport,roundsStarted,status")
                    throw new InvalidOperationException("Ralph workflow returned a malformed terminal result");
                var lastReport = ValueOf(record, "lastReport");
                if (roundsStartedInt == 1)
                {
                    if (lastReport is not null)
                        throw new InvalidOperationException("Ralph workflow returned an invalid first-round failure");
                    return new Dictionary<string, object?>
                    {
                        ["status"] = "round-failed",
                        ["roundsStarted"] = roundsStartedInt,
                    };
                }

                if (lastReport is null)
                    throw new InvalidOperationException("Ralph workflow returned a round failure without its last handoff");
                return new Dictionary<string, object?>
                {
                    ["status"] = "round-failed",
                    ["roundsStarted"] = roundsStartedInt,
                    ["lastReport"] = ReadReport(lastReport, "continue", maxHandoffChars),
                };
            }
            default:
                throw new InvalidOperationException("Ralph workflow returned an unknown terminal status");
        }
    }

    private static string? StopReasonError(WorkflowResult result)
        => result.StopReason switch
        {
            WorkflowStopReason.Completed => null,
            WorkflowStopReason.Cancelled => $"Ralph workflow was cancelled{(result.Error is null ? "" : $" ({result.Error})")}",
            WorkflowStopReason.Error => $"Ralph workflow failed: {result.Error ?? "unknown error"}",
            _ => $"Ralph workflow ended abnormally ({result.StopReason})",
        };

    private static string BoundResult(string text, long maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        if (maxChars <= TruncationNotice.Length)
            return TruncationNotice[..(int)maxChars];
        return $"{text[..(int)(maxChars - TruncationNotice.Length)]}{TruncationNotice}";
    }

    private static string RenderResult(Dictionary<string, object?> result, long maxChars)
    {
        var rounds = $"{result["roundsStarted"]} round{(result["roundsStarted"] is int count && count == 1 ? "" : "s")}";
        var status = result["status"] as string;
        var report = result["report"]!;
        var text = status switch
        {
            "complete" => $"Ralph worker reported completion after {rounds}.\nFinal report:\n{JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })}",
            "blocked" => $"Ralph worker reported a blocker after {rounds}.\nFinal report:\n{JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })}",
            "budget-limited" => $"Ralph reached its {rounds} limit; the worker reported work remaining.\nFinal report:\n{JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })}",
            _ => throw new InvalidOperationException("unknown Ralph status"),
        };
        return BoundResult(text, maxChars);
    }

    private static string RenderRoundFailure(Dictionary<string, object?> result, long maxChars)
    {
        var header = $"Ralph round {result["roundsStarted"]} child failed before producing a structured report.";
        var text = !result.TryGetValue("lastReport", out var lastReport) || lastReport is null
            ? $"{header}\nNo previous handoff was available."
            : $"{header}\nLast successful handoff:\n{JsonSerializer.Serialize(lastReport, new JsonSerializerOptions { WriteIndented = true })}";
        return BoundResult(text, maxChars);
    }

    private static string RenderValue(JsonElement value, long maxChars)
    {
        var result = WorkflowRealm.MaterializeFromRealm(value.GetProperty("result"), "result");
        var decoded = ReadRunResult(result, long.MaxValue, long.MaxValue);
        if (decoded["status"] as string == "round-failed")
            return RenderRoundFailure(decoded, maxChars);
        return RenderResult(decoded, maxChars);
    }

    private static JsonElement? PresentCall(JsonElement args)
    {
        var view = new JsonObject
        {
            ["card"] = "generic",
            ["title"] = "ralph",
            ["rawInput"] = args.TryGetProperty("objective", out var objective) ? JsonValue.Create(objective.GetString()) : null,
        };
        return JsonDocument.Parse(view.ToJsonString()).RootElement;
    }

    private static JsonObject ParameterSchema()
        => JsonNode.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["objective"],
              "properties": {
                "objective": {
                  "type": "string",
                  "description": "The immutable completion objective for every fresh Ralph round."
                },
                "maxRounds": {
                  "type": "number",
                  "description": "Optional positive safe-integer round cap, bounded by the deployment ceiling."
                }
              }
            }
            """)!.AsObject();

    private static long? LongOf(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict?.GetValueOrDefault(key) switch
        {
            long value => value,
            int value => value,
            _ => null,
        };

    private static object? ValueOf(IDictionary<string, object?> dict, string key)
        => dict.TryGetValue(key, out var value) ? value : null;

    private sealed class DisposeBundle(IReadOnlyList<IDisposable> disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}