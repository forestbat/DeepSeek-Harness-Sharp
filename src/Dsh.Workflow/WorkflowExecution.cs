using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Ptc;

namespace Dsh.Workflow;

public sealed record WorkflowExecutionObserver(
    Action<string> Phase,
    Action<string> Log,
    Action<WorkflowAgentInfo> AgentStart,
    Action<WorkflowAgentEndInfo> AgentEnd);

public sealed class WorkflowExecution
{
    private sealed record AgentOptionsResult(
        string? Label = null,
        string? Phase = null,
        string? Provider = null,
        string? Model = null,
        object? Schema = null);

    private readonly object _sync = new();
    private readonly string _program;
    private readonly WorkerLimits _limits;
    private readonly WorkflowExecutionObserver _observer;
    private readonly IChildPort _children;
    private readonly SubprocessService _subprocess;
    private readonly string _cwd;
    private readonly CancellationTokenSource _cts = new();
    private int _started;
    private int _activeSlots;
    private readonly List<TaskCompletionSource> _slotWaiters = [];
    private string? _cancelReason;
    private WorkflowError? _cancelError;
    private string? _currentPhase;

    public WorkflowExecution(
        string program,
        WorkerLimits limits,
        WorkflowExecutionObserver observer,
        IChildPort children,
        SubprocessService subprocess,
        string cwd)
    {
        _program = program;
        _limits = limits;
        _observer = observer;
        _children = children;
        _subprocess = subprocess;
        _cwd = cwd;
    }

    public int AgentsStarted
    {
        get
        {
            lock (_sync)
                return _started;
        }
    }

    public bool IsCancelled
    {
        get
        {
            lock (_sync)
                return _cancelReason is not null;
        }
    }

    public void Cancel(string reason)
    {
        WorkflowError? error;
        List<TaskCompletionSource> waiters;
        lock (_sync)
        {
            if (_cancelReason is not null)
                return;
            _cancelReason = reason;
            error = _cancelError = new WorkflowError($"workflow run cancelled: {reason}", WorkflowErrorCodes.Cancelled);
            waiters = [.. _slotWaiters];
            _slotWaiters.Clear();
        }

        foreach (var waiter in waiters)
            waiter.TrySetException(error);
        _cts.Cancel();
    }

    public async Task<WorkflowResult> DriveAsync()
    {
        try
        {
            ThrowIfCancelled();
            var client = new PtcScriptHostClient(
                _subprocess,
                _cwd,
                WorkflowProgram.Bindings,
                DispatchAsync,
                maxPendingCalls: Math.Max(_limits.MaxItemsPerCall, PtcScriptHostClient.MaxPendingCalls));
            var outcome = await client.RunAsync(_program, RunTimeoutMs(), _cts.Token);
            if (IsCancelled || outcome.Kind == PtcFailureKinds.Abort)
                return CancelledResult();
            if (!outcome.Ok)
            {
                return new WorkflowResult
                {
                    Value = null,
                    StopReason = WorkflowStopReason.Error,
                    Error = ComposeFailure(outcome),
                    AgentsStarted = AgentsStarted,
                };
            }

            object? value = null;
            if (outcome.Value is not null)
            {
                try
                {
                    value = WorkflowRealm.MaterializeFromRealm(outcome.Value, "workflow result");
                }
                catch (MaterializeError error)
                {
                    throw new WorkflowError(
                        $"the workflow's return value is not plain JSON data - {error.Message}. Return only JSON-serializable objects/arrays/scalars.",
                        WorkflowErrorCodes.ResultUnserializable,
                        error);
                }
            }

            return new WorkflowResult
            {
                Value = value,
                StopReason = WorkflowStopReason.Completed,
                AgentsStarted = AgentsStarted,
            };
        }
        catch (Exception error)
        {
            if (IsCancelled)
                return CancelledResult();
            return new WorkflowResult
            {
                Value = null,
                StopReason = WorkflowStopReason.Error,
                Error = WorkflowRealm.RenderThrown(error),
                AgentsStarted = AgentsStarted,
            };
        }
    }

    private long RunTimeoutMs() => _limits.RunTimeoutMs > 0 ? _limits.RunTimeoutMs : -1;

    private async Task<PtcToolReply> DispatchAsync(PtcToolCall call, CancellationToken signal)
    {
        _ = signal;
        switch (call.ToolName)
        {
            case "agent":
                return await AgentCallAsync(call.Arguments);
            case "phase":
                {
                    var title = call.Arguments["title"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(title))
                        return new PtcToolReply(false, null, "phase() requires a non-empty title string");
                    Phase(title);
                    return new PtcToolReply(true, null, null);
                }
            case "log":
                {
                    var message = call.Arguments["message"]?.GetValue<string>();
                    if (message is null)
                        return new PtcToolReply(false, null, "log() requires a message string");
                    Log(message);
                    return new PtcToolReply(true, null, null);
                }
            default:
                return new PtcToolReply(false, null, $"unknown workflow binding \"{call.ToolName}\"");
        }
    }

    private async Task<PtcToolReply> AgentCallAsync(JsonObject arguments)
    {
        var prompt = arguments["prompt"]?.GetValue<string>();
        if (string.IsNullOrEmpty(prompt))
            return new PtcToolReply(false, null, "agent() requires a non-empty prompt string");
        try
        {
            var value = await AgentAsync(prompt, arguments["opts"]);
            return new PtcToolReply(true, WorkflowJson.ToNode(value), null);
        }
        catch (Exception error)
        {
            return new PtcToolReply(false, null, WorkflowRealm.RenderThrown(error));
        }
    }

    private static string ComposeFailure(PtcRunOutcome outcome)
    {
        var message = $"[{outcome.Kind}] {outcome.Message}";
        return string.IsNullOrEmpty(outcome.Logs) ? message : $"{message}\n[logs]\n{outcome.Logs}";
    }

    private void ThrowIfCancelled()
    {
        if (IsCancelled)
            throw CancelledError();
    }

    private WorkflowError CancelledError()
    {
        lock (_sync)
            return _cancelError ?? new WorkflowError("workflow run cancelled", WorkflowErrorCodes.Cancelled);
    }

    private WorkflowResult CancelledResult()
    {
        var error = CancelledError();
        return new WorkflowResult
        {
            Value = null,
            StopReason = WorkflowStopReason.Cancelled,
            Error = error.Message,
            AgentsStarted = AgentsStarted,
        };
    }

    private void Phase(string title)
    {
        lock (_sync)
            _currentPhase = title;
        _observer.Phase(title);
    }

    private void Log(string message)
        => _observer.Log(message);

    private async Task<object?> AgentAsync(string prompt, JsonNode? rawOpts)
    {
        ThrowIfCancelled();
        var opts = ReadAgentOptions(rawOpts);
        var (seq, label, phase) = NextAgent(opts, prompt);
        await AcquireSlotAsync();
        try
        {
            ThrowIfCancelled();
            IChildHandle run;
            try
            {
                run = await _children.StartAsync(new ChildStartRequest(prompt, opts.Schema, opts.Provider, opts.Model));
            }
            catch (Exception error)
            {
                if (IsCancelled)
                    throw CancelledError();
                throw new WorkflowError($"agent() could not start a child: {WorkflowRealm.RenderThrown(error)}", WorkflowErrorCodes.AgentStart, error);
            }

            if (IsCancelled)
            {
                await run.DisposeAsync();
                throw CancelledError();
            }

            var info = new WorkflowAgentInfo(seq, label, phase, SessionId.Create(run.Id));
            _observer.AgentStart(info);
            try
            {
                ChildResult result;
                try
                {
                    result = await run.Result;
                }
                catch (Exception error)
                {
                    if (IsCancelled)
                    {
                        _observer.AgentEnd(End(info, WorkflowAgentOutcome.Cancelled));
                        throw CancelledError();
                    }

                    _observer.AgentEnd(End(info, WorkflowAgentOutcome.Failed));
                    throw new WorkflowError($"child agent run failed: {WorkflowRealm.RenderThrown(error)}", WorkflowErrorCodes.AgentResult, error);
                }

                if (result.StopReason == WorkflowStopReason.Completed)
                {
                    if (opts.Schema is not null)
                    {
                        if (result.Structured is null)
                        {
                            _observer.AgentEnd(End(info, WorkflowAgentOutcome.Failed));
                            return null;
                        }

                        _observer.AgentEnd(End(info, WorkflowAgentOutcome.Completed));
                        return result.Structured;
                    }

                    _observer.AgentEnd(End(info, WorkflowAgentOutcome.Completed));
                    return OutputText(result.Output);
                }

                if (IsCancelled)
                {
                    _observer.AgentEnd(End(info, WorkflowAgentOutcome.Cancelled));
                    throw CancelledError();
                }

                _observer.AgentEnd(End(info, WorkflowAgentOutcome.Failed));
                return null;
            }
            finally
            {
                await run.DisposeAsync();
            }
        }
        finally
        {
            ReleaseSlot();
        }
    }

    private static AgentOptionsResult ReadAgentOptions(JsonNode? rawOpts)
    {
        if (rawOpts is null)
            return new AgentOptionsResult();
        object? opts;
        try
        {
            opts = WorkflowRealm.MaterializeFromRealm(rawOpts, "agent() options");
        }
        catch (MaterializeError error)
        {
            throw new WorkflowError($"agent() options must be plain JSON data - {error.Message}", WorkflowErrorCodes.InvalidArgument, error);
        }

        if (opts is null)
            return new AgentOptionsResult();
        if (opts is not IDictionary<string, object?> record)
            throw new WorkflowError("agent() options must be an object", WorkflowErrorCodes.InvalidArgument);
        var supported = new HashSet<string>(StringComparer.Ordinal) { "label", "phase", "schema", "provider", "model" };
        var deferred = new HashSet<string>(StringComparer.Ordinal) { "effort", "isolation", "agentType" };
        foreach (var key in record.Keys)
        {
            if (supported.Contains(key))
                continue;
            if (deferred.Contains(key))
            {
                throw new WorkflowError(
                    $"agent() option \"{key}\" is deferred and not supported by this engine (supported: label, phase, schema, provider, model)",
                    WorkflowErrorCodes.UnsupportedOption);
            }

            throw new WorkflowError(
                $"agent() option \"{key}\" is not recognized (supported: label, phase, schema, provider, model)",
                WorkflowErrorCodes.UnsupportedOption);
        }

        foreach (var key in new[] { "label", "phase", "provider", "model" })
        {
            if (record.TryGetValue(key, out var value) && value is not null && value is not string)
                throw new WorkflowError($"agent() option \"{key}\" must be a string", WorkflowErrorCodes.InvalidArgument);
        }

        object? schema = null;
        if (record.TryGetValue("schema", out var rawSchema) && rawSchema is not null)
        {
            AssertObjectJsonSchema(rawSchema);
            schema = rawSchema;
        }

        return new AgentOptionsResult(
            ValueOf(record, "label") as string,
            ValueOf(record, "phase") as string,
            ValueOf(record, "provider") as string,
            ValueOf(record, "model") as string,
            schema);
    }

    private static object? ValueOf(IDictionary<string, object?> dict, string key)
        => dict.TryGetValue(key, out var value) ? value : null;

    private static void AssertObjectJsonSchema(object? schema)
    {
        object? node;
        try
        {
            node = JsonSerializer.SerializeToNode(schema);
        }
        catch (Exception error)
        {
            throw new WorkflowError($"agent() schema is outside the supported subset - {WorkflowRealm.RenderThrown(error)}", WorkflowErrorCodes.UnsupportedSchema, error);
        }

        if (node is not JsonObject obj
            || obj["type"]?.GetValue<string>() != "object")
        {
            throw new WorkflowError(
                "agent() schema is outside the supported subset - schema.type must be \"object\" (structured output is object-rooted)",
                WorkflowErrorCodes.UnsupportedSchema);
        }

        try
        {
            JsonSchemaValidator.AssertSupported(obj);
        }
        catch (Exception error)
        {
            throw new WorkflowError($"agent() schema is outside the supported subset - {WorkflowRealm.RenderThrown(error)}", WorkflowErrorCodes.UnsupportedSchema, error);
        }
    }

    private (int Seq, string Label, string? Phase) NextAgent(AgentOptionsResult opts, string prompt)
    {
        lock (_sync)
        {
            if (_cancelReason is not null)
                throw CancelledError();
            if (_started >= _limits.MaxTotalAgents)
            {
                throw new WorkflowError(
                    $"this run reached its total agent cap ({_limits.MaxTotalAgents}) - a runaway-loop backstop; raise the applicable maxTotalAgents limit if the scale is intentional",
                    WorkflowErrorCodes.AgentCap);
            }

            _started++;
            return (_started, opts.Label ?? DefaultLabel(prompt), opts.Phase ?? _currentPhase);
        }
    }

    private Task AcquireSlotAsync()
    {
        lock (_sync)
        {
            if (_activeSlots < _limits.MaxConcurrentAgents)
            {
                _activeSlots++;
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _slotWaiters.Add(waiter);
            return waiter.Task;
        }
    }

    private void ReleaseSlot()
    {
        lock (_sync)
        {
            if (_slotWaiters.Count > 0)
            {
                var next = _slotWaiters[0];
                _slotWaiters.RemoveAt(0);
                next.TrySetResult();
            }
            else
            {
                _activeSlots--;
            }
        }
    }

    private static WorkflowAgentEndInfo End(WorkflowAgentInfo info, string outcome)
        => new(info.Seq, info.Label, info.Phase, info.ChildId, outcome);

    private static string DefaultLabel(string prompt)
    {
        var newline = prompt.IndexOf('\n');
        var line = newline == -1 ? prompt : prompt[..newline];
        return line.Length <= 48 ? line : $"{line[..47]}…";
    }

    private static string OutputText(IReadOnlyList<ContentBlock> blocks)
        => string.Concat(blocks.OfType<TextBlock>().Select(block => block.Text));
}
