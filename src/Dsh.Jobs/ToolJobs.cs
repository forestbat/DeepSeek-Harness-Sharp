using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Jobs;

public enum CompletionDelivery
{
    Quiet,
    Wakeup,
}

public sealed record ToolJobsConfig
{
    public long WaitTimeoutMs { get; init; } = 30_000;
    public long MaxWaitTimeoutMs { get; init; } = 600_000;
    public CompletionDelivery CompletionDelivery { get; init; } = CompletionDelivery.Wakeup;
    public int MaxConsecutiveWakes { get; init; } = 3;
}

public sealed record PublicJobSnapshot(
    string Id,
    string Kind,
    string Label,
    string Status,
    string? Detail,
    long StartedAt,
    long? FinishedAt);

public static class ToolJobs
{
    public const string PluginName = "tool-jobs";

    private const string SectionText = "Track every background job id you start. You are notified in-session when a job finishes — do not busy-poll or sleep on one; keep working on independent steps and do not duplicate a running job's work. Before giving a final answer, collect every still-relevant job with job_output (set wait: true only when you are genuinely blocked on it), and job_kill jobs that stopped mattering.";

    private const string JobOutputDescription =
        "Read a background job. Stream jobs return only output since the previous read; "
        + "final-output jobs return their result after settlement. Every response ends with "
        + "`[status: ...]`. Reads are non-blocking unless `wait: true`, which waits up to the configured cap.";

    private const string JobListDescription =
        "List your background jobs (running and finished) with their ids, kinds, and statuses.";

    private const string JobKillDescription =
        "Request cancellation of a running background job by job id. Returns immediately; the job settles as killed once its work actually stops.";

    private static readonly JsonObject PublicJobSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["id", "kind", "label", "status", "startedAt"],
          "properties": {
            "id": { "type": "string" },
            "kind": { "type": "string" },
            "label": { "type": "string" },
            "status": { "type": "string", "enum": ["running", "stopping", "completed", "killed", "failed"] },
            "detail": { "type": "string" },
            "startedAt": { "type": "integer" },
            "finishedAt": { "type": "integer" }
          }
        }
        """);

    public static IDisposable Register(Context ctx, ToolJobsConfig? config = null)
    {
        var resolved = config ?? new ToolJobsConfig();
        var waitDefault = resolved.WaitTimeoutMs;
        var waitCap = resolved.MaxWaitTimeoutMs;
        var delivery = resolved.CompletionDelivery;
        var wakeBudget = resolved.MaxConsecutiveWakes;
        if (waitDefault > waitCap)
            throw new ArgumentException($"tool-jobs: waitTimeoutMs ({waitDefault}) exceeds maxWaitTimeoutMs ({waitCap})");
        if (wakeBudget < 1)
            throw new ArgumentException($"tool-jobs: maxConsecutiveWakes ({wakeBudget}) must be a whole number of turns");

        var tools = ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var jobs = ctx.Get<JobsService>(JobsService.ServiceName)!;
        var disposables = new List<IDisposable>();

        var spentWakes = new ConditionalWeakTable<IAgent, StrongBox<int>>();
        if (delivery == CompletionDelivery.Wakeup)
        {
            var offClaimed = ctx.On(AgentEventNames.InboxClaimed, (_, args) =>
            {
                var payload = args[0];
                if (payload is null) return new ValueTask<object?>();
                var agent = payload.GetType().GetProperty("Agent")?.GetValue(payload) as IAgent;
                var message = payload.GetType().GetProperty("Message")?.GetValue(payload) as UserMessage;
                if (agent is not null && message?.Source is UserMessageSource)
                    spentWakes.Remove(agent);
                return new ValueTask<object?>();
            }, new EventOptions { Global = true });
            disposables.Add(new ActionDisposable(() => offClaimed()));
        }

        var outputLimits = new ConditionalWeakTable<ToolExecution, StrongBox<int>>();
        var offPreExecute = ctx.On(ToolRuntime.PreExecuteEvent, (_, args) =>
        {
            var exec = (ToolExecution)args[0]!;
            var next = (Func<ValueTask<object?>>)args[^1]!;
            if (VisibleOutputLimit(jobs, exec) is { } maxBytes)
                outputLimits.AddOrUpdate(exec, new StrongBox<int>(maxBytes));
            return next();
        }, new EventOptions { Prepend = true, Global = true });
        disposables.Add(new ActionDisposable(() => offPreExecute()));

        IReadOnlyList<ContentBlock>? FinalizeTaskContent(ToolExecution exec, ToolExecutionResult result)
        {
            int? maxBytes = null;
            if (outputLimits.TryGetValue(exec, out var box))
            {
                maxBytes = box.Value;
                outputLimits.Remove(exec);
            }
            maxBytes ??= VisibleOutputLimit(jobs, exec);
            if (maxBytes is null) return null;
            if (exec.Name == "job_output" && !result.IsError && result is ToolExecutionResult.Success success)
            {
                var value = success.Value;
                var text = value.GetProperty("text").GetString() ?? "";
                var job = value.GetProperty("job");
                var body = text.Length > 0 ? text : "(no new output)";
                var content = body.EndsWith('\n') ? body[..^1] : body;
                var suffix = $"\n{StatusLine(job)}";
                if (RawSingleText(result.Content) == content + suffix)
                    return [new TextBlock(FitWithSuffix(content, suffix, maxBytes.Value, "\n[output truncated]"))];
            }
            return BoundSingleText(result.Content, maxBytes.Value);
        }

        disposables.Add(jobs.AttachController(PluginName));

        disposables.Add(systemPrompt.Section(PromptSection.Literal("tool:jobs", PromptOrders.ToolJobs, SectionText)));

        disposables.Add(jobs.OnJobDone((snapshot, owner) =>
        {
            if (snapshot.Reported || owner is null) return;
            var message = MessageFactory.CreateUserMessage(
                [new TextBlock(FitCompletionNotice(snapshot))],
                new PluginMessageSource(PluginName, ContextForms.Notice, Summary: CompletionSummary(snapshot)));
            var spent = spentWakes.TryGetValue(owner, out var counter) ? counter.Value : 0;
            if (delivery == CompletionDelivery.Wakeup && owner.Status == AgentStatus.Idle && spent < wakeBudget)
            {
                spentWakes.AddOrUpdate(owner, new StrongBox<int>(spent + 1));
                owner.Followup(message);
                return;
            }
            owner.Inject(message);
        }));

        disposables.Add(tools.Register(new ToolDefinition
        {
            Name = "job_output",
            Description = JobOutputDescription,
            Parameters = ParseSchema("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["job_id"],
                  "properties": {
                    "job_id": { "type": "string", "description": "Job id returned by the tool that started the background work." },
                    "wait": { "type": "boolean", "description": "Block until the job reaches a terminal status or the timeout expires. A timed-out wait returns [status: running] and leaves the job alive." },
                    "timeout_ms": { "type": "number", "description": "Max wait in milliseconds (only meaningful with wait: true). Defaults to the configured wait timeout; capped by the configured maximum." }
                  }
                }
                """),
            Output = new ToolOutputDefinition(
                ParseSchema($$"""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["text", "job"],
                  "properties": {
                    "text": { "type": "string" },
                    "job": {{PublicJobSchema.ToJsonString()}}
                  }
                }
                """),
                (_, value) =>
                {
                    var text = value.GetProperty("text").GetString() ?? "";
                    var job = value.GetProperty("job");
                    var body = text.Length > 0 ? text : "(no new output)";
                    var separator = body.EndsWith('\n') ? "" : "\n";
                    return [new TextBlock($"{body}{separator}{StatusLine(job)}")];
                }),
            FinalizeContent = FinalizeTaskContent,
            Execute = async (args, exec) =>
            {
                var id = ValidateJobId(args);
                if (args.TryGetProperty("wait", out var waitElement) && waitElement.ValueKind == JsonValueKind.True)
                {
                    double? timeoutArg = args.TryGetProperty("timeout_ms", out var timeoutElement) && timeoutElement.ValueKind == JsonValueKind.Number
                        ? timeoutElement.GetDouble()
                        : null;
                    var timeout = Math.Min(timeoutArg ?? waitDefault, waitCap);
                    await jobs.WaitAsync(id, timeout, exec.Agent, exec.Signal);
                }
                var read = jobs.Read(id, exec.Agent);
                return new { text = read.Text, job = PublicJob(read.Snapshot) };
            },
        }));

        disposables.Add(tools.Register(new ToolDefinition
        {
            Name = "job_list",
            Description = JobListDescription,
            Parameters = ParseSchema("""{ "type": "object", "additionalProperties": false, "properties": {} }"""),
            Output = new ToolOutputDefinition(
                ParseSchema($$"""
                {
                  "type": "array",
                  "items": {{PublicJobSchema.ToJsonString()}}
                }
                """),
                (_, value) =>
                {
                    var jobs = value.EnumerateArray().ToList();
                    return
                    [
                        new TextBlock(jobs.Count == 0
                            ? "(no background jobs)"
                            : string.Join('\n', jobs.Select(job =>
                                $"{job.GetProperty("id").GetString()} [{job.GetProperty("kind").GetString()}] {job.GetProperty("status").GetString()} — {job.GetProperty("label").GetString()}")))
                    ];
                }),
            Execute = (args, exec) =>
            {
                IReadOnlyList<PublicJobSnapshot> visible = jobs.List(exec.Agent).Select(PublicJob).ToList();
                return Task.FromResult<object?>(visible);
            },
        }));

        disposables.Add(tools.Register(new ToolDefinition
        {
            Name = "job_kill",
            Description = JobKillDescription,
            Parameters = ParseSchema("""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["job_id"],
                  "properties": {
                    "job_id": { "type": "string", "description": "Job id returned by the tool that started the background work." },
                    "reason": { "type": "string", "description": "Optional short reason, recorded in the log and forwarded to the job." }
                  }
                }
                """),
            Output = new ToolOutputDefinition(
                ParseSchema($$"""
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["outcome", "job"],
                  "properties": {
                    "outcome": { "type": "string", "enum": ["cancellation-requested", "already-finished"] },
                    "job": {{PublicJobSchema.ToJsonString()}}
                  }
                }
                """),
                (_, value) =>
                {
                    var outcome = value.GetProperty("outcome").GetString();
                    var job = value.GetProperty("job");
                    var id = job.GetProperty("id").GetString();
                    return
                    [
                        new TextBlock(outcome == "already-finished"
                            ? $"job {id} had already finished {StatusLine(job)}"
                            : $"requested cancellation of job {id}")
                    ];
                }),
            FinalizeContent = FinalizeTaskContent,
            Execute = (args, exec) =>
            {
                var id = ValidateJobId(args);
                var reason = args.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
                    ? reasonElement.GetString()
                    : null;
                var outcome = jobs.Kill(id, exec.Agent, reason);
                var snapshot = PublicJob(jobs.Get(id, exec.Agent));
                return Task.FromResult<object?>(new
                {
                    outcome = outcome == JobKillOutcome.AlreadyFinished ? "already-finished" : "cancellation-requested",
                    job = snapshot,
                });
            },
        }));

        return new CompositeDisposable(disposables);
    }

    public static string StatusLine(JobSnapshot snapshot)
        => snapshot.Detail is not null
            ? $"[status: {JobStatusWire.Of(snapshot.Status)}, {snapshot.Detail}]"
            : $"[status: {JobStatusWire.Of(snapshot.Status)}]";

    private static string StatusLine(JsonElement job)
    {
        var status = job.GetProperty("status").GetString();
        var detail = job.TryGetProperty("detail", out var detailElement) && detailElement.ValueKind == JsonValueKind.String
            ? detailElement.GetString()
            : null;
        return detail is not null ? $"[status: {status}, {detail}]" : $"[status: {status}]";
    }

    private static PublicJobSnapshot PublicJob(JobSnapshot snapshot)
        => new(
            snapshot.Id,
            snapshot.Kind,
            snapshot.Label,
            JobStatusWire.Of(snapshot.Status),
            snapshot.Detail,
            snapshot.StartedAt,
            snapshot.FinishedAt);

    private static string ValidateJobId(JsonElement args)
    {
        var value = args.TryGetProperty("job_id", out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException($"invalid job_id: expected a non-empty string, got {JsonSerializer.Serialize(value)}");
        return value;
    }

    private static int? VisibleOutputLimit(JobsService jobs, ToolExecution exec)
    {
        if (exec.Name is not ("job_output" or "job_kill")) return null;
        if (!exec.Arguments.TryGetProperty("job_id", out var element) || element.ValueKind != JsonValueKind.String)
            return null;
        var jobId = element.GetString();
        if (string.IsNullOrEmpty(jobId)) return null;
        return jobs.List(exec.Agent).FirstOrDefault(snapshot => snapshot.Id == jobId)?.OutputLimitBytes;
    }

    private static string? RawSingleText(IReadOnlyList<ContentBlock> content)
        => content.Count == 1 && content[0] is TextBlock text ? text.Text : null;

    private static IReadOnlyList<ContentBlock>? BoundSingleText(IReadOnlyList<ContentBlock> content, int maxBytes)
        => RawSingleText(content) is { } text
            ? [new TextBlock(FitWithSuffix(text, "", maxBytes, "\n[result truncated]"))]
            : null;

    private static string FitWithSuffix(string content, string suffix, int maxBytes, string omitted)
    {
        var complete = $"{content}{suffix}";
        if (Utf8Retention.ByteCount(complete) <= maxBytes) return complete;
        var fixedPart = $"{(content.EndsWith(omitted.TrimStart()) ? "" : omitted)}{suffix}";
        var fixedBytes = Utf8Retention.ByteCount(fixedPart);
        if (fixedBytes >= maxBytes) return Utf8Retention.RetainTail(fixedPart, maxBytes);
        return $"{Utf8Retention.RetainTail(content, maxBytes - fixedBytes)}{fixedPart}";
    }

    private static string CompletionSummary(JobSnapshot snapshot)
        => MessageFactory.BoundContextSummary($"{snapshot.Kind} {snapshot.Label} {StatusLine(snapshot)}");

    private static string FitCompletionNotice(JobSnapshot snapshot)
    {
        var prefix = $"background job {snapshot.Id}";
        var detail = $" ({snapshot.Kind}: {snapshot.Label}) finished {StatusLine(snapshot)}";
        const string action = "\nDone; job_output.";
        var complete = $"{prefix}{detail}. Read its output with job_output.";
        if (snapshot.OutputLimitBytes is not { } maxBytes || Utf8Retention.ByteCount(complete) <= maxBytes)
            return complete;
        const string omitted = "\n[notice truncated]";
        var fixedPart = $"{prefix}{omitted}{action}";
        var fixedBytes = Utf8Retention.ByteCount(fixedPart);
        if (fixedBytes <= maxBytes)
        {
            return fixedBytes == maxBytes
                ? fixedPart
                : $"{prefix}{Utf8Retention.RetainHead(detail, maxBytes - fixedBytes)}{omitted}{action}";
        }
        var compact = $"{prefix}{action}";
        var compactBytes = Utf8Retention.ByteCount(compact);
        if (compactBytes <= maxBytes) return compact;
        var actionBytes = Utf8Retention.ByteCount(action);
        if (actionBytes >= maxBytes) return Utf8Retention.RetainTail(action, maxBytes);
        return $"{Utf8Retention.RetainHead(prefix, maxBytes - actionBytes)}{action}";
    }

    private static JsonObject ParseSchema(string json) => JsonNode.Parse(json)!.AsObject();

    private sealed class CompositeDisposable(IReadOnlyList<IDisposable> disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            dispose();
        }
    }
}
