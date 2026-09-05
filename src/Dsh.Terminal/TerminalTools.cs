using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis;
using Dsh.Core;
using Dsh.Jobs;
using Dsh.Llm;

namespace Dsh.Terminal;

public sealed record TerminalToolsConfig
{
    public const int DefaultMaxResultBytes = 256 * 1024;
    public const int MinMaxResultBytes = 64;

    public bool EnableRunInBackground { get; init; } = true;
    public int MaxResultBytes { get; init; } = DefaultMaxResultBytes;
}

public static class TerminalTools
{
    public const string PluginName = "tool-terminal";

    private const string SectionText =
        "Use a terminal session only when work needs persistent terminal state or interactive stdin; prefer shell/read/write/edit for bounded one-shot operations. Track every terminal session id and close sessions that no longer matter. An inferred_idle or timeout result does not prove the foreground command exited.";

    private const string OpenDescription =
        "Create a persistent, owner-isolated terminal session from a registered backend type. Use this for shell or REPL state that must survive across tool calls.";

    private const string SendDescription =
        "Send text to a persistent terminal. By default Enter is submitted and the call waits for a prompt, stdin wait, output silence, timeout, or session exit.";

    private const string ReadDescription =
        "Read a bounded page of retained output from a persistent terminal without sending input.";

    private const string SignalDescription =
        "Send an allowed signal to the current foreground process group of a persistent terminal.";

    private const string CloseDescription =
        "Close one persistent terminal and wait until its captured owned process tree is gone.";

    private const string ListDescription =
        "List persistent terminal sessions owned by the current agent.";

    private static readonly JsonObject SessionStatusSchema = ParseSchema("""
        {
          "oneOf": [
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "kind": { "type": "string", "const": "running" }
              },
              "required": ["kind"]
            },
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "kind": { "type": "string", "const": "exited" },
                "exitCode": { "oneOf": [{ "type": "integer" }, { "type": "null" }] },
                "signal": { "oneOf": [{ "type": "string" }, { "type": "null" }] }
              },
              "required": ["kind", "exitCode", "signal"]
            }
          ]
        }
        """);

    private static readonly JsonObject SessionSnapshotSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "sessionId": { "type": "string" },
            "name": { "type": "string" },
            "type": { "type": "string" },
            "pid": { "type": "integer" },
            "status": null
          },
          "required": ["sessionId", "type", "status"]
        }
        """);

    private static readonly JsonObject SpawnResultSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "sessionId": { "type": "string" },
            "name": { "type": "string" },
            "type": { "type": "string" },
            "pid": { "type": "integer" },
            "status": null,
            "motd": { "type": "string" }
          },
          "required": ["sessionId", "type", "status", "motd"]
        }
        """);

    private static readonly JsonObject BackgroundTaskOutputSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "kind": { "type": "string", "const": "background" },
            "jobId": { "type": "string" }
          },
          "required": ["kind", "jobId"]
        }
        """);

    private static readonly JsonObject ForegroundSendSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "kind": { "type": "string", "const": "foreground" },
            "viewport": { "type": "string" },
            "waitReason": { "type": "string", "enum": ["stdin_read", "inferred_idle", "timeout", "session_exit"] },
            "sessionStatus": null,
            "truncated": { "type": "boolean" }
          },
          "required": ["kind", "viewport", "waitReason", "sessionStatus", "truncated"]
        }
        """);

    private static readonly JsonObject SendOutputSchema = ParseSchema("""
        {
          "oneOf": []
        }
        """);

    private static readonly JsonObject ReadOutputSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "text": { "type": "string" },
            "totalLines": { "type": "integer" },
            "lineBegin": { "type": "integer" },
            "lineEnd": { "type": "integer" },
            "truncated": { "type": "boolean" }
          },
          "required": ["text", "totalLines", "lineBegin", "lineEnd", "truncated"]
        }
        """);

    private static readonly JsonObject SignalOutputSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "delivered": { "type": "boolean", "const": true },
            "targetPgid": { "type": "integer" }
          },
          "required": ["delivered", "targetPgid"]
        }
        """);

    private static readonly JsonObject CloseOutputSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "sessionId": { "type": "string" },
            "outcome": { "type": "string", "enum": ["closed", "already-closing"] }
          },
          "required": ["sessionId", "outcome"]
        }
        """);

    private static readonly JsonObject ListOutputSchema = ParseSchema("""
        {
          "type": "array",
          "items": null
        }
        """);

    static TerminalTools()
    {
        SessionSnapshotSchema["properties"]!.AsObject()["status"] = SessionStatusSchema.DeepClone();
        SpawnResultSchema["properties"]!.AsObject()["status"] = SessionStatusSchema.DeepClone();
        ForegroundSendSchema["properties"]!.AsObject()["sessionStatus"] = SessionStatusSchema.DeepClone();
        SendOutputSchema["oneOf"] = new JsonArray(BackgroundTaskOutputSchema.DeepClone(), ForegroundSendSchema.DeepClone());
        ListOutputSchema["items"] = SessionSnapshotSchema.DeepClone();
    }

    public static IDisposable Register(Context ctx, TerminalToolsConfig? config = null)
    {
        var resolved = config ?? new TerminalToolsConfig();
        var enableRunInBackground = resolved.EnableRunInBackground;
        var maxResultBytes = resolved.MaxResultBytes;
        if (maxResultBytes < TerminalToolsConfig.MinMaxResultBytes)
            throw new InvalidOperationException($"tool-terminal: maxResultBytes must be a safe integer of at least {TerminalToolsConfig.MinMaxResultBytes}");
        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var terminals = ctx.Get<TerminalSessionService>(TerminalSessionService.ServiceName)!;
        var disposables = new List<IDisposable>();

        disposables.Add(systemPrompt.Section(PromptSection.Literal("tool:pty", PromptOrders.ToolPty, SectionText)));

        IReadOnlyList<ContentBlock>? FinalizeContent(ToolExecution _, ToolExecutionResult result)
        {
            var raw = RawContentText(result.Content);
            return raw is null ? null : [new TextBlock(TerminalRendering.BoundTerminalText(raw, maxResultBytes))];
        }

        disposables.Add(tools.Register(new ToolDefinition
        {
            Name = "terminal_open",
            Description = OpenDescription,
            Parameters = ParseSchema("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["type"],
                  "properties": {
                    "type": { "type": "string", "description": "Registered terminal backend type, usually \"shell\"." },
                    "name": { "type": "string", "description": "Optional owner-local display name such as \"main\" or \"gdb\"." },
                    "cwd": { "type": "string", "description": "Initial working directory. Defaults to the deployment workspace root." }
                  }
                }
                """),
            Output = new ToolOutputDefinition(SpawnResultSchema, (_, value) =>
                [new TextBlock(TerminalRendering.RenderSpawn(Deserialize<TerminalSpawnResult>(value), maxResultBytes))]),
            FinalizeContent = FinalizeContent,
            Execute = async (args, exec) =>
            {
                var type = args.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString() ?? ""
                    : "";
                if (type.Length == 0)
                    throw new InvalidOperationException("type must be a non-empty string");
                var name = args.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;
                var cwd = args.TryGetProperty("cwd", out var cwdElement) && cwdElement.ValueKind == JsonValueKind.String
                    ? cwdElement.GetString()
                    : null;
                return await terminals.Spawn(RequireAgent(exec.Agent), new TerminalSpawnRequest(type, name, cwd), exec.Signal);
            },
        }));

        var sendDescription = enableRunInBackground
            ? $"{SendDescription} Background mode returns a job id for job_output/job_kill."
            : SendDescription;
        var sendParameters = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("sessionId", "text"),
            ["properties"] = new JsonObject
            {
                ["sessionId"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Terminal session id returned by terminal_open or terminal_list.",
                },
                ["text"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "UTF-8 text to write to the terminal.",
                },
                ["submit"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Submit Enter after text (default true). Set false for control characters or incomplete REPL input.",
                },
            },
        };
        if (enableRunInBackground)
        {
            sendParameters["properties"]!.AsObject()["run_in_background"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Return a job id immediately; collect with job_output or stop with job_kill.",
            };
        }

        disposables.Add(tools.Register(new ToolDefinition
        {
            Name = "terminal_send",
            Description = sendDescription,
            Parameters = sendParameters,
            Output = new ToolOutputDefinition(SendOutputSchema, (_, value) =>
            {
                var kind = value.GetProperty("kind").GetString();
                return
                [
                    new TextBlock(kind == "background"
                        ? $"started background job {value.GetProperty("jobId").GetString()}"
                        : TerminalRendering.RenderSend(RenderSendValue(value), maxResultBytes))
                ];
            }),
            FinalizeContent = FinalizeContent,
            Execute = async (args, exec) =>
            {
                var owner = RequireAgent(exec.Agent);
                var id = SessionId(args);
                var text = args.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                    ? textElement.GetString() ?? ""
                    : "";
                var submit = args.TryGetProperty("submit", out var submitElement) && submitElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? submitElement.GetBoolean()
                    : true;
                if (args.TryGetProperty("run_in_background", out var backgroundElement) && backgroundElement.ValueKind == JsonValueKind.True)
                {
                    if (!enableRunInBackground)
                        throw new InvalidOperationException("background terminal sends are disabled by tool-terminal configuration");
                    var jobs = ctx.Get<JobsService>(JobsService.ServiceName, false)
                        ?? throw new InvalidOperationException("background terminal sends require @deepseek-ai/dsh-jobs and @deepseek-ai/dsh-tool-jobs");
                    var cancelRequested = false;
                    var jobId = jobs.Start(new JobStart
                    {
                        Kind = "pty-send",
                        Label = $"{id}: {(text.Length > 0 ? text : "(input)")}",
                        Owner = owner,
                        OutputLimitBytes = maxResultBytes,
                        Run = () =>
                        {
                            var operation = terminals.StartSend(owner, id, new TerminalSendRequest(text, submit));
                            return new JobHooks(
                                Cancel: _ =>
                                {
                                    cancelRequested = true;
                                    operation.Cancel();
                                },
                                Done: SendJobDone(operation, () => cancelRequested),
                                ReadOutput: () => TerminalRendering.RenderSendRead(operation.ReadOutput()));
                        },
                    });
                    return new { kind = "background", jobId };
                }
                var foreground = terminals.StartSend(owner, id, new TerminalSendRequest(text, submit, exec.Signal));
                var result = await foreground.Done;
                if (exec.Signal.IsCancellationRequested)
                    throw new InvalidOperationException("terminal send aborted");
                return new
                {
                    kind = "foreground",
                    viewport = result.Viewport,
                    waitReason = WaitReasonText(result.WaitReason),
                    sessionStatus = result.SessionStatus,
                    truncated = result.Truncated,
                };
            },
        }));

        disposables.Add(tools.Register(new ToolDefinition
        {
            Name = "terminal_read",
            Description = ReadDescription,
            Parameters = ParseSchema("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["sessionId"],
                  "properties": {
                    "sessionId": { "type": "string", "description": "Terminal session id." },
                    "offset": { "type": "number", "description": "Newest-relative line offset (default 0)." },
                    "count": { "type": "number", "description": "Requested line count (default 500; backend caps apply)." }
                  }
                }
                """),
            Output = new ToolOutputDefinition(ReadOutputSchema, (_, value) =>
                [new TextBlock(TerminalRendering.RenderRead(Deserialize<TerminalReadResult>(value), maxResultBytes))]),
            FinalizeContent = FinalizeContent,
            Execute = (args, exec) =>
            {
                var result = terminals.Read(RequireAgent(exec.Agent), SessionId(args), new TerminalReadRequest(
                    args.TryGetProperty("offset", out var offset) && offset.ValueKind == JsonValueKind.Number ? offset.GetInt32() : null,
                    args.TryGetProperty("count", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : null));
                return Task.FromResult<object?>(result);
            },
        }));

        disposables.Add(tools.Register(new ToolDefinition
        {
            Name = "terminal_signal",
            Description = SignalDescription,
            Parameters = ParseSchema("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["sessionId", "signal"],
                  "properties": {
                    "sessionId": { "type": "string", "description": "Terminal session id." },
                    "signal": { "type": "string", "enum": ["SIGINT", "SIGTERM", "SIGKILL", "SIGTSTP", "SIGHUP"], "description": "Signal to deliver. Shell-targeted SIGKILL is rejected; use terminal_close." }
                  }
                }
                """),
            Output = new ToolOutputDefinition(SignalOutputSchema, (args, value) =>
            {
                var signal = args.TryGetProperty("signal", out var signalElement) && signalElement.ValueKind == JsonValueKind.String
                    ? signalElement.GetString()
                    : "";
                var targetPgid = value.GetProperty("targetPgid").GetInt32();
                return [new TextBlock($"delivered {signal} to foreground process group {targetPgid}")];
            }),
            FinalizeContent = FinalizeContent,
            Execute = async (args, exec) =>
            {
                var signalText = args.TryGetProperty("signal", out var signalElement) && signalElement.ValueKind == JsonValueKind.String
                    ? signalElement.GetString() ?? ""
                    : "";
                var signal = ParseSignal(signalText);
                return (object?)(await terminals.Signal(RequireAgent(exec.Agent), SessionId(args), signal));
            },
        }));

        disposables.Add(tools.Register(new ToolDefinition
        {
            Name = "terminal_close",
            Description = CloseDescription,
            Parameters = ParseSchema("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["sessionId"],
                  "properties": {
                    "sessionId": { "type": "string", "description": "Terminal session id." }
                  }
                }
                """),
            Output = new ToolOutputDefinition(CloseOutputSchema, (_, value) =>
            {
                var sessionId = value.GetProperty("sessionId").GetString();
                var outcome = value.GetProperty("outcome").GetString();
                return
                [
                    new TextBlock(outcome == "closed"
                        ? $"closed terminal session {sessionId}"
                        : $"terminal session {sessionId} was already closing")
                ];
            }),
            FinalizeContent = FinalizeContent,
            Execute = async (args, exec) =>
            {
                var id = SessionId(args);
                var closed = await terminals.Kill(RequireAgent(exec.Agent), id);
                return new { sessionId = id.Value, outcome = closed ? "closed" : "already-closing" };
            },
        }));

        disposables.Add(tools.Register(new ToolDefinition
        {
            Name = "terminal_list",
            Description = ListDescription,
            Parameters = ParseSchema("""{ "type": "object", "additionalProperties": false, "properties": {} }"""),
            Output = new ToolOutputDefinition(ListOutputSchema, (_, value) =>
                [new TextBlock(TerminalRendering.RenderList(value.Deserialize<IReadOnlyList<TerminalSessionSnapshot>>(DshJson.Options) ?? [], maxResultBytes))]),
            FinalizeContent = FinalizeContent,
            Execute = (args, exec) => Task.FromResult<object?>(terminals.List(RequireAgent(exec.Agent))),
        }));

        return new CompositeDisposable(disposables);
    }

    private static IAgent RequireAgent(IAgent? agent)
        => agent ?? throw new InvalidOperationException("terminal tools require an initiating agent");

    private static TerminalSessionId SessionId(JsonElement args)
    {
        var value = args.TryGetProperty("sessionId", out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException("sessionId must be a non-empty string");
        return new TerminalSessionId(value);
    }

    private static string? RawContentText(IReadOnlyList<ContentBlock> content)
    {
        if (content.Count != 1 || content[0] is not TextBlock block)
            return null;
        return block.Text;
    }

    private static string SendDetail(TerminalSendResult result)
        => result.SessionStatus.Kind == "running"
            ? $"wait: {WaitReasonText(result.WaitReason)}"
            : $"session exited: {result.SessionStatus.ExitCode?.ToString() ?? result.SessionStatus.Signal ?? "unknown"}";

    private static async Task<JobOutcome> SendJobDone(TerminalSendOperation operation, Func<bool> cancelRequested)
    {
        try
        {
            var result = await operation.Done;
            return new JobOutcome(cancelRequested() ? JobStatus.Killed : JobStatus.Completed, Detail: SendDetail(result));
        }
        catch (Exception error)
        {
            return new JobOutcome(JobStatus.Failed, Detail: error.Message);
        }
    }

    private static TerminalSignal ParseSignal(string value) => value switch
    {
        "SIGINT" => TerminalSignal.SIGINT,
        "SIGTERM" => TerminalSignal.SIGTERM,
        "SIGKILL" => TerminalSignal.SIGKILL,
        "SIGTSTP" => TerminalSignal.SIGTSTP,
        "SIGHUP" => TerminalSignal.SIGHUP,
        _ => throw new ArgumentException($"invalid signal: {value}"),
    };

    private static string WaitReasonText(TerminalWaitReason reason) => reason switch
    {
        TerminalWaitReason.StdinRead => "stdin_read",
        TerminalWaitReason.InferredIdle => "inferred_idle",
        TerminalWaitReason.Timeout => "timeout",
        TerminalWaitReason.SessionExit => "session_exit",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    private static TerminalSendResult RenderSendValue(JsonElement value)
        => new(
            value.GetProperty("viewport").GetString() ?? "",
            ParseWaitReason(value.GetProperty("waitReason").GetString() ?? ""),
            value.GetProperty("sessionStatus").Deserialize<TerminalSessionStatus>(DshJson.Options)
                ?? TerminalSessionStatus.Running(),
            value.GetProperty("truncated").GetBoolean());

    private static TerminalWaitReason ParseWaitReason(string value) => value switch
    {
        "stdin_read" => TerminalWaitReason.StdinRead,
        "inferred_idle" => TerminalWaitReason.InferredIdle,
        "timeout" => TerminalWaitReason.Timeout,
        "session_exit" => TerminalWaitReason.SessionExit,
        _ => throw new ArgumentException($"invalid waitReason: {value}"),
    };

    private static T Deserialize<T>(JsonElement value)
        => value.Deserialize<T>(DshJson.Options) ?? throw new JsonException($"terminal result value is malformed for {typeof(T).Name}");

    private static JsonObject ParseSchema(string json) => JsonNode.Parse(json)!.AsObject();

    private sealed class CompositeDisposable(IReadOnlyList<IDisposable> disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }
}
