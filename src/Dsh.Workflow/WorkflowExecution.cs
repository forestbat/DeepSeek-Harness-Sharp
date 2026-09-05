using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;
using Jint;
using Jint.Constraints;
using Jint.Native;

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
    private readonly WorkflowMeta _meta;
    private readonly string _body;
    private readonly object? _args;
    private readonly WorkerLimits _limits;
    private readonly WorkflowExecutionObserver _observer;
    private readonly IChildPort _children;
    private readonly CancellationTokenSource _cts = new();
    private int _started;
    private int _activeSlots;
    private readonly List<TaskCompletionSource> _slotWaiters = [];
    private string? _cancelReason;
    private WorkflowError? _cancelError;
    private string? _currentPhase;

    public WorkflowExecution(
        WorkflowMeta meta,
        string body,
        object? args,
        WorkerLimits limits,
        WorkflowExecutionObserver observer,
        IChildPort children)
    {
        _meta = meta;
        _body = body;
        _args = args;
        _limits = limits;
        _observer = observer;
        _children = children;
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
        WorkflowError? error = null;
        List<TaskCompletionSource> waiters = [];
        lock (_sync)
        {
            if (_cancelReason is not null)
                return;
            _cancelReason = reason;
            error = _cancelError = new WorkflowError($"workflow run cancelled: {reason}", WorkflowErrorCodes.Cancelled);
            waiters = [.._slotWaiters];
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
            using var engine = CreateEngine();
            var evaluated = await engine.EvaluateAsync(BuildScript(), $"workflow:{_meta.Name}", _cts.Token);
            if (IsCancelled)
                throw CancelledError();
            var raw = evaluated.ToObject();
            if (raw is not null)
            {
                try
                {
                    raw = WorkflowRealm.MaterializeFromRealm(raw, "workflow result");
                }
                catch (MaterializeError error)
                {
                    throw new WorkflowError(
                        $"the workflow's return value is not plain JSON data — {error.Message}. Return only JSON-serializable objects/arrays/scalars.",
                        WorkflowErrorCodes.ResultUnserializable,
                        error);
                }
            }

            return new WorkflowResult
            {
                Value = raw,
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

    private Engine CreateEngine()
    {
        var engine = new Engine(options =>
        {
            options.TimeoutInterval(TimeSpan.FromMilliseconds(_limits.SyncTimeoutMs));
            options.CancellationToken(_cts.Token);
        });
        engine.SetValue("__hostIsCancelled", new Func<bool>(() => IsCancelled));
        engine.SetValue("__phase", new Action<string>(Phase));
        engine.SetValue("__log", new Action<string>(Log));
        engine.SetValue("__agent", new Func<string, object?, Task<object?>>(AgentAsync));
        engine.SetValue("__args", _args ?? JsValue.Undefined);
        return engine;
    }

    private const string ScriptTemplate = """
        (async () => {
          const __workflowError = (message, code) => {
            const error = new Error(message);
            error.name = 'WorkflowError';
            error.code = code;
            error.__workflowFatal = true;
            return error;
          };
          const __cancelledError = (message) => {
            const error = __workflowError(message ?? 'workflow run cancelled', 'CANCELLED');
            error.__workflowCancelled = true;
            return error;
          };
          const __isCancelled = () => __hostIsCancelled();
          const __fatalInfo = (error) => {
            if (error && error.__workflowFatal) {
              return { message: error.message || '', code: error.code || '', fatal: true };
            }
            const inner = error && error.InnerException;
            if (inner && inner.Fatal === true) {
              return { message: inner.Message || '', code: inner.Code || '', fatal: true };
            }
            return null;
          };
          const __renderThrown = (error) => {
            const inner = error && error.InnerException;
            if (inner && typeof inner.Message === 'string' && inner.Message.length > 0) return inner.Message;
            if (error && typeof error.message === 'string' && error.message.length > 0) return error.message;
            return String(error);
          };
          const __isFatal = (error) => __fatalInfo(error) !== null;
          const agent = async (prompt, opts) => {
            if (__isCancelled()) throw __cancelledError();
            if (typeof prompt !== 'string' || prompt.length === 0) throw __workflowError('agent() requires a non-empty prompt string', 'INVALID_ARGUMENT');
            try {
              return await __agent(prompt, opts);
            } catch (error) {
              const info = __fatalInfo(error);
              if (info) {
                if (info.code === 'CANCELLED') throw __cancelledError(info.message);
                throw __workflowError(info.message, info.code);
              }
              throw __workflowError(`agent() could not start a child: ${__renderThrown(error)}`, 'AGENT_START');
            }
          };
          const parallel = async (thunks) => {
            if (__isCancelled()) throw __cancelledError();
            if (!Array.isArray(thunks)) throw __workflowError('parallel() requires an array of zero-argument functions', 'INVALID_ARGUMENT');
            if (thunks.length > __MAX_ITEMS__) throw __workflowError('parallel() received ' + thunks.length + ' items — over the per-call cap (__MAX_ITEMS__); split the work or raise maxItemsPerCall in the engine config', 'ITEM_CAP');
            return await Promise.all(thunks.map(async (thunk, index) => {
              if (typeof thunk !== 'function') throw __workflowError('parallel() item ' + index + ' is not a function', 'INVALID_ARGUMENT');
              try {
                return await thunk();
              } catch (error) {
                if (__isFatal(error)) throw error;
                return null;
              }
            }));
          };
          const pipeline = async (items, ...stages) => {
            if (__isCancelled()) throw __cancelledError();
            if (!Array.isArray(items)) throw __workflowError('pipeline() requires an items array', 'INVALID_ARGUMENT');
            if (items.length > __MAX_ITEMS__) throw __workflowError('pipeline() received ' + items.length + ' items — over the per-call cap (__MAX_ITEMS__); split the work or raise maxItemsPerCall in the engine config', 'ITEM_CAP');
            if (stages.length === 0) throw __workflowError('pipeline() requires at least one stage function', 'INVALID_ARGUMENT');
            const mapped = stages.map((stage, index) => {
              if (typeof stage !== 'function') throw __workflowError('pipeline() stage ' + index + ' is not a function', 'INVALID_ARGUMENT');
              return stage;
            });
            return await Promise.all(items.map(async (item, index) => {
              let value = item;
              try {
                for (const stage of mapped) value = await stage(value, item, index);
                return value;
              } catch (error) {
                if (__isFatal(error)) throw error;
                return null;
              }
            }));
          };
          const phase = (title) => {
            if (__isCancelled()) throw __cancelledError();
            if (typeof title !== 'string' || title.length === 0) throw __workflowError('phase() requires a non-empty title string', 'INVALID_ARGUMENT');
            __phase(title);
          };
          const log = (message) => {
            if (__isCancelled()) throw __cancelledError();
            if (typeof message !== 'string') throw __workflowError('log() requires a message string', 'INVALID_ARGUMENT');
            __log(message);
          };
          const args = __args;
          __BODY__
        })()
        """;

    private string BuildScript()
    {
        var maxItems = _limits.MaxItemsPerCall.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ScriptTemplate
            .Replace("__MAX_ITEMS__", maxItems)
            .Replace("__BODY__", _body);
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

    private async Task<object?> AgentAsync(object? rawPrompt, object? rawOpts)
    {
        ThrowIfCancelled();
        if (rawPrompt is not string prompt || prompt.Length == 0)
            throw new WorkflowError("agent() requires a non-empty prompt string", WorkflowErrorCodes.InvalidArgument);
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

    private AgentOptionsResult ReadAgentOptions(object? rawOpts)
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
            throw new WorkflowError($"agent() options must be plain JSON data — {error.Message}", WorkflowErrorCodes.InvalidArgument, error);
        }

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
            throw new WorkflowError($"agent() schema is outside the supported subset — {WorkflowRealm.RenderThrown(error)}", WorkflowErrorCodes.UnsupportedSchema, error);
        }

        if (node is not JsonObject obj
            || obj["type"]?.GetValue<string>() != "object")
        {
            throw new WorkflowError(
                "agent() schema is outside the supported subset — schema.type must be \"object\" (structured output is object-rooted)",
                WorkflowErrorCodes.UnsupportedSchema);
        }

        try
        {
            JsonSchemaValidator.AssertSupported(obj);
        }
        catch (Exception error)
        {
            throw new WorkflowError($"agent() schema is outside the supported subset — {WorkflowRealm.RenderThrown(error)}", WorkflowErrorCodes.UnsupportedSchema, error);
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
                    $"this run reached its total agent cap ({_limits.MaxTotalAgents}) — a runaway-loop backstop; raise the applicable maxTotalAgents limit if the scale is intentional",
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