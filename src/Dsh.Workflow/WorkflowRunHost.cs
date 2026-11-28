using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Agent;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Sdk;

namespace Dsh.Workflow;

public interface IRoutedChildPort : IChildPort
{
    Task<IChildHandle> StartAsync(ChildStartRequest request, string? target, AgentBackendKind? backend);
}

public sealed class WorkflowRunHost : IWorkflowRun
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource<WorkflowResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WorkflowExecution _execution;
    private readonly CancellationTokenSource _controller = new();
    private readonly int _disposeGraceMs;
    private Task _disposeTask = Task.CompletedTask;
    private string? _cancelReason;
    private bool _settled;
    private bool _disposeStarted;

    public WorkflowRunHost(
        WorkflowRunId id,
        WorkflowMeta meta,
        WorkflowExecution execution,
        int disposeGraceMs,
        CancellationTokenSource? controller = null)
    {
        Id = id;
        Meta = meta;
        _execution = execution;
        _disposeGraceMs = disposeGraceMs;
        _controller = controller ?? new CancellationTokenSource();
        Task.Run(() => execution.DriveAsync())
            .ContinueWith(task =>
            {
                var outcome = task.IsCanceled
                    ? new WorkflowResult { Value = null, StopReason = WorkflowStopReason.Cancelled, Error = "workflow run cancelled", AgentsStarted = execution.AgentsStarted }
                    : task.IsFaulted
                        ? new WorkflowResult { Value = null, StopReason = WorkflowStopReason.Error, Error = WorkflowRealm.RenderThrown(task.Exception), AgentsStarted = execution.AgentsStarted }
                        : task.Result;
                Settle(outcome);
            }, TaskScheduler.Default);
    }

    public WorkflowRunId Id { get; }

    public WorkflowMeta Meta { get; }

    public Task<WorkflowResult> Result => _result.Task;

    public void Cancel(string? reason = null)
    {
        lock (_sync)
        {
            if (_settled || _cancelReason is not null)
                return;
            _cancelReason = reason ?? "workflow cancelled";
        }

        _execution.Cancel(_cancelReason);
        _controller.Cancel();
        _ = Task.Delay(_disposeGraceMs).ContinueWith(_ => ForceSettle());
    }

    public Task DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeStarted)
                return _disposeTask;
            _disposeStarted = true;
            _disposeTask = AwaitDisposeAsync();
        }

        Cancel("workflow disposed");
        return _disposeTask;
    }

    private async Task AwaitDisposeAsync()
    {
        await Task.WhenAny(Result, Task.Delay(_disposeGraceMs));
        ForceSettle();
    }

    private void ForceSettle()
    {
        WorkflowResult result;
        lock (_sync)
        {
            if (_settled)
                return;
            _settled = true;
            result = new WorkflowResult
            {
                Value = null,
                StopReason = WorkflowStopReason.Cancelled,
                Error = _cancelReason is null ? "workflow run cancelled" : $"workflow run cancelled: {_cancelReason}",
                AgentsStarted = _execution.AgentsStarted,
            };
        }

        _result.TrySetResult(result);
    }

    private void Settle(WorkflowResult result)
    {
        lock (_sync)
        {
            if (_settled)
                return;
            _settled = true;
        }

        _result.TrySetResult(result);
    }

    internal static IChildHandle StartRemoteChild(
        IAgentExecutionRouter router,
        ChildStartRequest request,
        string? target,
        AgentBackendKind? backend,
        string? cwd,
        MessagePrincipal? sender,
        CancellationToken cancellationToken)
        => new RemoteChildHandle(router, request, target, backend, cwd, sender, cancellationToken);

    public sealed class SubagentChildPort(ISubagentService subagents, string provider, IAgent parent, ToolCallId? parentCallId, CancellationTokenSource controller) : IRoutedChildPort
    {
        public async Task<IChildHandle> StartAsync(ChildStartRequest request)
        {
            var run = await subagents.StartAsync(provider, new SubagentStartRequest
            {
                Prompt = [new TextBlock(request.Prompt)],
                Parent = parent,
                ParentCallId = parentCallId,
                Signal = controller.Token,
                OutputSchema = request.Schema is null ? null : ToJsonObject(request.Schema),
                AgentOptions = request.Provider is null && request.Model is null
                    ? null
                    : new AgentOptions(request.Provider, request.Model),
            });
            return new HostChildHandle(run);
        }

        public Task<IChildHandle> StartAsync(ChildStartRequest request, string? target, AgentBackendKind? backend)
        {
            var isLocal = (string.IsNullOrWhiteSpace(target) || string.Equals(target, "local", StringComparison.OrdinalIgnoreCase))
                && (backend is null || backend == AgentBackendKind.Local);
            if (isLocal)
                return StartAsync(request);

            var router = parent.Ctx.Get<IAgentExecutionRouter>(IAgentExecutionRouter.ServiceName, false)
                ?? throw new WorkflowError(
                    "remote workflow agent dispatch requires the agentExecutionRouter service",
                    WorkflowErrorCodes.AgentStart);
            var sender = new MessagePrincipal(PrincipalKind.Agent, parent.AgentId.Value, parent.Id.Value);
            return Task.FromResult(StartRemoteChild(
                router, request, target, backend, parent.Session.WorkingDirectory, sender, controller.Token));
        }

        private static JsonObject ToJsonObject(object value)
            => DshJson.ToNodeRuntime(value)?.AsObject()
                ?? throw new InvalidOperationException("schema could not be serialized");
    }

    private sealed class HostChildHandle(ISubagentRun run) : IChildHandle
    {
        public string Id => run.Id.Value;

        public Task<ChildResult> Result => run.Result.ContinueWith(
            task => new ChildResult(
                task.Result.Output,
                WorkflowRealm.MaterializeFromRealm(task.Result.Structured, "child result"),
                SubagentStopReasonWire.Of(task.Result.StopReason)),
            TaskContinuationOptions.ExecuteSynchronously);

        public Task DisposeAsync() => run.DisposeAsync();
    }

    private sealed class RemoteChildHandle : IChildHandle
    {
        private const int PollIntervalMs = 50;

        private readonly CancellationTokenSource _cts;
        private IAgentExecutionHandle? _handle;

        public RemoteChildHandle(
            IAgentExecutionRouter router,
            ChildStartRequest request,
            string? target,
            AgentBackendKind? backend,
            string? cwd,
            MessagePrincipal? sender,
            CancellationToken cancellationToken)
        {
            Id = $"remote-{Guid.NewGuid():N}";
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Result = DriveAsync(router, request, target, backend, cwd, sender);
        }

        public string Id { get; }

        public Task<ChildResult> Result { get; }

        private async Task<ChildResult> DriveAsync(
            IAgentExecutionRouter router,
            ChildStartRequest request,
            string? target,
            AgentBackendKind? backend,
            string? cwd,
            MessagePrincipal? sender)
        {
            var dispatch = new AgentDispatchRequest(
                TargetId: string.IsNullOrWhiteSpace(target) ? null : target,
                PreferredBackend: backend,
                Prompt: request.Prompt,
                Cwd: cwd,
                WaitForCompletion: true,
                Sender: sender);
            var handle = await router.DispatchAsync(dispatch, _cts.Token).ConfigureAwait(false);
            _handle = handle;
            AgentExecutionSnapshot snapshot;
            while (true)
            {
                _cts.Token.ThrowIfCancellationRequested();
                snapshot = await handle.GetStatusAsync(_cts.Token).ConfigureAwait(false);
                if (snapshot.Completed)
                    break;
                await Task.Delay(PollIntervalMs, _cts.Token).ConfigureAwait(false);
            }
            if (!snapshot.Completed)
                throw new WorkflowError("remote agent did not complete", WorkflowErrorCodes.AgentResult);
            if (snapshot.Error is { Length: > 0 })
            {
                return new ChildResult(
                    snapshot.Output is { Length: > 0 } partial ? [new TextBlock(partial)] : [],
                    null,
                    WorkflowAgentOutcome.Failed);
            }
            var text = snapshot.Output ?? "";
            IReadOnlyList<ContentBlock> blocks = text.Length > 0 ? [new TextBlock(text)] : [];
            object? structured = null;
            if (request.Schema is not null)
            {
                if (string.IsNullOrWhiteSpace(text))
                    throw new WorkflowError("remote agent returned empty JSON for schema", WorkflowErrorCodes.AgentResult);
                var node = JsonNode.Parse(text)
                    ?? throw new WorkflowError("remote agent returned empty JSON for schema", WorkflowErrorCodes.AgentResult);
                var schemaObject = DshJson.ToNodeRuntime(request.Schema)?.AsObject()
                    ?? throw new WorkflowError("workflow schema could not be serialized", WorkflowErrorCodes.UnsupportedSchema);
                using var doc = JsonDocument.Parse(node.ToJsonString());
                var violations = JsonSchemaValidator.Validate(schemaObject, doc.RootElement, "value");
                if (violations.Count > 0)
                    throw new WorkflowError($"remote agent output failed schema validation: {string.Join("; ", violations)}", WorkflowErrorCodes.AgentResult);
                structured = WorkflowRealm.MaterializeFromRealm(node, "remote child result");
            }
            return new ChildResult(blocks, structured, SubagentStopReasonWire.Of(SubagentStopReason.Completed));
        }

        public async Task DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_handle is { } handle)
            {
                if (!Result.IsCompleted)
                {
                    try
                    {
                        await handle.CancelAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }
                try
                {
                    await handle.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
            _cts.Dispose();
        }
    }
}
