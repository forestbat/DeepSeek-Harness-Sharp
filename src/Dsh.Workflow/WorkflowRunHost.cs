using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Subagent;

namespace Dsh.Workflow;

public sealed class WorkflowRunHost : IWorkflowRun
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource<WorkflowResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WorkflowExecution _execution;
    private readonly CancellationTokenSource _controller = new();
    private readonly int _disposeGraceMs;
    private readonly Task _driveTask;
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
        _driveTask = Task.Run(() => execution.DriveAsync())
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

    public sealed class SubagentChildPort(SubagentRuntime subagents, string provider, IAgent parent, CancellationTokenSource controller) : IChildPort
    {
        public async Task<IChildHandle> StartAsync(ChildStartRequest request)
        {
            var run = await subagents.StartAsync(provider, new SubagentStartRequest
            {
                Prompt = [new TextBlock(request.Prompt)],
                Parent = parent,
                Signal = controller.Token,
                OutputSchema = request.Schema is null ? null : ToJsonObject(request.Schema),
                AgentOptions = request.Provider is null && request.Model is null
                    ? null
                    : new AgentOptions(request.Provider, request.Model),
            });
            return new HostChildHandle(run);
        }

        private static System.Text.Json.Nodes.JsonObject ToJsonObject(object value)
            => JsonSerializer.SerializeToNode(value, DshJson.Options)?.AsObject()
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
}