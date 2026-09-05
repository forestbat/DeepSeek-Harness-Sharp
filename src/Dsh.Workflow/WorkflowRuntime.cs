using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Workflow;

public sealed record WorkflowStartRequest
{
    public required string Script { get; init; }
    public required object? Meta { get; init; }
    public object? Args { get; init; }
    public string? SubagentProvider { get; init; }
    public int? MaxTotalAgents { get; init; }
    public required IAgent Parent { get; init; }
    public CancellationToken Signal { get; init; }
}

public interface IWorkflowRun
{
    WorkflowRunId Id { get; }
    WorkflowMeta Meta { get; }
    Task<WorkflowResult> Result { get; }
    void Cancel(string? reason = null);
    Task DisposeAsync();
}

public sealed record WorkerLimits(
    int MaxConcurrentAgents,
    int MaxTotalAgents,
    int MaxItemsPerCall,
    int SyncTimeoutMs);

public sealed record ChildStartRequest(
    string Prompt,
    object? Schema = null,
    string? Provider = null,
    string? Model = null);

public sealed record ChildResult(
    IReadOnlyList<ContentBlock> Output,
    object? Structured,
    string StopReason);

public interface IChildHandle
{
    string Id { get; }
    Task<ChildResult> Result { get; }
    Task DisposeAsync();
}

public interface IChildPort
{
    Task<IChildHandle> StartAsync(ChildStartRequest request);
}