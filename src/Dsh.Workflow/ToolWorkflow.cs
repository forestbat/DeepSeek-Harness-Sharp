using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Workflow;

public sealed record ToolWorkflowConfig
{
    public string ToolName { get; init; } = "workflow";
    public int MaxResultChars { get; init; } = 50_000;
}

public sealed record ToolWorkflowRunStartPayload(WorkflowRunId RunId, string Name) : SessionEventPayload
{
    public const string EventType = "tool-workflow/run-start";
    public override string Type => EventType;
}

public sealed record ToolWorkflowAgentStartPayload(
    WorkflowRunId RunId,
    int Seq,
    string Label,
    string? Phase,
    SessionId ChildId) : SessionEventPayload
{
    public const string EventType = "tool-workflow/agent-start";
    public override string Type => EventType;
}

public sealed record ToolWorkflowAgentEndPayload(
    WorkflowRunId RunId,
    int Seq,
    string Outcome) : SessionEventPayload
{
    public const string EventType = "tool-workflow/agent-end";
    public override string Type => EventType;
}

public sealed record ToolWorkflowRunEndPayload(
    WorkflowRunId RunId,
    string StopReason) : SessionEventPayload
{
    public const string EventType = "tool-workflow/run-end";
    public override string Type => EventType;
}

public static class ToolWorkflow
{
    public const string Description = """
        Run a JavaScript workflow script that orchestrates subagents at scale. Use this for work that fans out across many independent pieces — an audit over many files, a migration, multi-angle research, adversarial verification of findings — where you write the orchestration as a script instead of delegating turn by turn.

        The workflow's identity rides the `meta` parameter as JSON: required `name` (short kebab-case) and `description` strings, optional `whenToUse` string and `phases` array (`{title, detail?, provider?, model?}`). The `script` parameter is the plain JavaScript body ONLY (NOT TypeScript, and NO `export const meta` statement — meta is a parameter, not code), running with top-level await; end with `return <value>` — the value must be JSON-serializable and is this tool's result.

        Script-body hooks:
        - `agent(prompt, opts?): Promise<any>` — run one subagent to completion. Without `opts.schema` it resolves to the child's final text; with `opts.schema` (an object-rooted JSON Schema using ONLY type/properties/required/additionalProperties/items/enum/const/oneOf — no pattern/format/numeric bounds) it resolves to the validated object. Resolves `null` when the child fails (filter with `.filter(Boolean)`). Other opts: `label` (display), `phase` (progress group), and independent `provider`/`model` LLM target overrides (either may be provided alone). Anything else (`effort`/`isolation`/`agentType`) is rejected loudly.
        - `pipeline(items, ...stages): Promise<any[]>` — run each item through the stages independently with NO barrier between stages (prefer this for multi-stage work). Each stage receives `(prev, item, index)`. An ordinary stage throw drops that ITEM to `null` and skips its remaining stages.
        - `parallel(thunks): Promise<any[]>` — run zero-argument functions concurrently and await ALL of them (a barrier; use only when a stage genuinely needs every prior result together). A throwing thunk resolves to `null`.
        - `phase(title)` — start a progress phase; `log(message)` — narrate progress; `args` — the tool call's `args` input, verbatim.

        Misused hooks (bad arguments, unknown options, unsupported schemas, tripped caps) throw errors that ALWAYS kill the script — they never dissolve into a per-item `null`.

        Constraints: concurrency and total-agent caps apply; no filesystem, network, timers, or Node.js APIs are provided — the agents do the work, the script only coordinates them. The run executes in the foreground: this call returns when the whole script finishes.
        """;

    public static IDisposable Apply(Context ctx, object? config)
    {
        var resolved = ResolveConfig(config);
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var workflow = ctx.Get<WorkflowEngine>(WorkflowEngine.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var recorder = new WorkflowRecorder(ctx);
        var agentStartSubscription = ctx.On("workflow/agent-start", (_, args) =>
        {
            recorder.OnAgentStart(_, args);
            return new ValueTask<object?>();
        }, new EventOptions { Global = true });
        var agentEndSubscription = ctx.On("workflow/agent-end", (_, args) =>
        {
            recorder.OnAgentEnd(_, args);
            return new ValueTask<object?>();
        }, new EventOptions { Global = true });
        var prompt = systemPrompt.Section(PromptSection.Literal(
            $"tool:{resolved.ToolName}",
            PromptOrders.ToolWorkflow,
            $"Use the {resolved.ToolName} tool ONLY when the user explicitly asks for a workflow or for large multi-agent orchestration: you write a JavaScript script (the tool description documents the exact format) that fans work out across many subagents with phases and structured results. For one or two delegations, prefer plain subagent calls."));
        var registration = tools.Register(BuildDefinition(workflow, recorder, resolved));
        return new DisposeBundle([prompt, registration, new FuncDispose(agentStartSubscription), new FuncDispose(agentEndSubscription)]);
    }

    public static IDisposable Apply(Context ctx, ToolWorkflowConfig config) => Apply(ctx, (object?)config);

    private static ToolWorkflowConfig ResolveConfig(object? config)
    {
        if (config is ToolWorkflowConfig typed)
            return typed;
        var dict = config as IReadOnlyDictionary<string, object?>;
        return new ToolWorkflowConfig
        {
            ToolName = dict?.GetValueOrDefault("toolName") as string ?? "workflow",
            MaxResultChars = IntOf(dict, "maxResultChars") ?? 50_000,
        };
    }

    private static ToolDefinition BuildDefinition(WorkflowEngine workflow, WorkflowRecorder recorder, ToolWorkflowConfig config)
    {
        return new ToolDefinition
        {
            Name = config.ToolName,
            Description = Description,
            Parameters = ParameterSchema(),
            Output = new ToolOutputDefinition(
                new JsonObject(),
                (args, value) => [new TextBlock(RenderResult(args, value, config.MaxResultChars))],
                (args, _) => PresentWorkflowCall(args)),
            Execute = (args, exec) => ExecuteAsync(workflow, recorder, config, args, exec),
        };
    }

    private static async Task<object?> ExecuteAsync(
        WorkflowEngine workflow,
        WorkflowRecorder recorder,
        ToolWorkflowConfig config,
        JsonElement args,
        ToolRunContext exec)
    {
        var parent = exec.Agent
            ?? throw new InvalidOperationException("workflow tool requires a calling agent (exec.agent was undefined)");
        var run = workflow.Start(new WorkflowStartRequest
        {
            Script = args.GetProperty("script").GetString() ?? "",
            Meta = WorkflowRealm.MaterializeFromRealm(args.GetProperty("meta"), "meta"),
            Args = args.TryGetProperty("args", out var argsElement)
                ? WorkflowRealm.MaterializeFromRealm(argsElement, "args")
                : null,
            Parent = parent,
            Signal = exec.Signal,
        });
        var recordsRun = exec.Parent is null;
        if (recordsRun)
            recorder.Start(parent.Session, run);
        var abortRegistration = exec.Signal.Register(() => run.Cancel("parent step aborted"));

        WorkflowResult? result = null;
        try
        {
            result = await run.Result;
            var error = StopReasonError(result);
            if (error is not null)
                throw new InvalidOperationException(error);
            return new
            {
                runId = run.Id.Value,
                agentsStarted = result.AgentsStarted,
                result = result.Value,
            };
        }
        finally
        {
            abortRegistration.Dispose();
            try
            {
                await run.DisposeAsync();
                if (recordsRun)
                {
                    if (result is null)
                        throw new InvalidOperationException("workflow run settled without a result");
                    recorder.Finish(run.Id, result.StopReason);
                }
            }
            finally
            {
                if (recordsRun)
                    recorder.Abandon(run.Id);
            }
        }
    }

    internal static string? StopReasonError(WorkflowResult result)
        => result.StopReason switch
        {
            WorkflowStopReason.Completed => null,
            WorkflowStopReason.Cancelled => $"workflow run was cancelled{(result.Error is null ? "" : $" ({result.Error})")}",
            WorkflowStopReason.Error => $"workflow run failed: {result.Error ?? "unknown error"}",
            _ => $"workflow run ended abnormally ({result.StopReason})",
        };

    private static string RenderResult(JsonElement args, JsonElement value, int maxChars)
    {
        var name = args.TryGetProperty("meta", out var meta)
            && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("name", out var nameElement)
            && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? ""
                : "";
        var agentsStarted = value.GetProperty("agentsStarted").GetInt32();
        var resultNode = JsonNode.Parse(value.GetProperty("result").GetRawText());
        var rendered = resultNode?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
        var clipped = rendered.Length > maxChars
            ? $"{rendered[..maxChars]}\n… [truncated: {rendered.Length - maxChars} more characters]"
            : rendered;
        return $"workflow \"{name}\" completed ({agentsStarted} agent{(agentsStarted == 1 ? "" : "s")}).\nReturn value:\n{clipped}";
    }

    private static JsonElement? PresentWorkflowCall(JsonElement args)
    {
        var title = args.TryGetProperty("meta", out var meta)
            && meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String
                ? $"workflow: {name.GetString()}"
                : "workflow";
        var view = new JsonObject
        {
            ["card"] = "generic",
            ["title"] = title,
            ["rawInput"] = args.TryGetProperty("script", out var script) ? JsonValue.Create(script.GetString()) : null,
        };
        return JsonDocument.Parse(view.ToJsonString()).RootElement;
    }

    private static JsonObject ParameterSchema()
        => JsonNode.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["script", "meta"],
              "properties": {
                "script": {
                  "type": "string",
                  "description": "The plain-JS workflow script body (top-level await allowed; NO `export const meta` statement; end with `return <json-value>`)."
                },
                "meta": {
                  "type": "object",
                  "additionalProperties": true,
                  "required": ["name", "description"],
                  "description": "The workflow identity block (plain JSON — never code).",
                  "properties": {
                    "name": { "type": "string", "description": "Short kebab-case workflow name." },
                    "description": { "type": "string", "description": "One-line description of what the workflow does." },
                    "whenToUse": { "type": "string", "description": "Optional guidance on when this workflow applies." },
                    "phases": {
                      "type": "array",
                      "description": "Optional phase declarations matched by phase() calls.",
                      "items": {
                        "type": "object",
                        "additionalProperties": true,
                        "properties": {
                          "title": { "type": "string", "description": "The phase title phase() calls match by exact string." },
                          "detail": { "type": "string", "description": "Optional one-line description of the phase." },
                          "provider": { "type": "string", "description": "Optional provider override this phase is expected to use." },
                          "model": { "type": "string", "description": "Optional model override this phase is expected to use." }
                        }
                      }
                    }
                  }
                },
                "args": {
                  "type": "object",
                  "additionalProperties": true,
                  "description": "Optional JSON input exposed to the script as the `args` global (wrap a bare list as a field, e.g. {\"files\": [...]})."
                }
              }
            }
            """)!.AsObject();

    private static int? IntOf(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict?.GetValueOrDefault(key) switch
        {
            long value => checked((int)value),
            int value => value,
            _ => null,
        };

    private sealed class WorkflowRecorder(Context ctx)
    {
        private readonly Dictionary<WorkflowRunId, Session> _active = [];
        private readonly object _sync = new();

        public void Start(Session session, IWorkflowRun run)
        {
            if (Append(session, new ToolWorkflowRunStartPayload(run.Id, run.Meta.Name)))
            {
                lock (_sync)
                    _active[run.Id] = session;
            }
        }

        public void Finish(WorkflowRunId runId, string stopReason)
        {
            Session? session;
            lock (_sync)
            {
                _active.TryGetValue(runId, out session);
                _active.Remove(runId);
            }

            if (session is not null)
                Append(session, new ToolWorkflowRunEndPayload(runId, stopReason));
        }

        public void Abandon(WorkflowRunId runId)
        {
            lock (_sync)
                _active.Remove(runId);
        }

        private bool Append(Session session, SessionEventPayload payload)
        {
            try
            {
                session.Append(payload);
                return true;
            }
            catch (Exception error)
            {
                ctx.Logger.Warn($"tool-workflow: disabled durable record after {payload.Type} append failed: {WorkflowRealm.RenderThrown(error)}");
                return false;
            }
        }

        public void OnAgentStart(object? thisArg, object?[] args)
        {
            if (args[0] is not WorkflowRunInfo info || args[1] is not WorkflowAgentInfo agent)
                return;
            Session? session;
            lock (_sync)
                _active.TryGetValue(info.Id, out session);
            if (session is null)
                return;
            if (!Append(session, new ToolWorkflowAgentStartPayload(info.Id, agent.Seq, agent.Label, agent.Phase, agent.ChildId)))
            {
                lock (_sync)
                    _active.Remove(info.Id);
            }
        }

        public void OnAgentEnd(object? thisArg, object?[] args)
        {
            if (args[0] is not WorkflowRunInfo info || args[1] is not WorkflowAgentEndInfo agent)
                return;
            Session? session;
            lock (_sync)
                _active.TryGetValue(info.Id, out session);
            if (session is null)
                return;
            if (!Append(session, new ToolWorkflowAgentEndPayload(info.Id, agent.Seq, agent.Outcome)))
            {
                lock (_sync)
                    _active.Remove(info.Id);
            }
        }
    }

    private sealed class DisposeBundle(IReadOnlyList<IDisposable> disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }

    private sealed class FuncDispose(Func<bool> dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            dispose();
        }
    }
}