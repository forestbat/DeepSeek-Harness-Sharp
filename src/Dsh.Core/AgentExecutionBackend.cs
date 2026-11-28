using Dsh.Llm;

namespace Dsh.Core;

public sealed record RemoteExecutionTarget(
    string TargetId,
    AgentBackendKind Kind,
    string DisplayName,
    string? MachineName = null,
    string? Endpoint = null,
    bool Available = true,
    IReadOnlyList<string>? Capabilities = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record AgentDispatchRequest(
    string? TargetId = null,
    AgentBackendKind? PreferredBackend = null,
    string? Prompt = null,
    string? Cwd = null,
    string? Name = null,
    AgentId? AgentId = null,
    SessionId? SessionId = null,
    bool WaitForCompletion = true,
    MessagePrincipal? Sender = null,
    string? AgentPreset = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record AgentExecutionSnapshot(
    AgentId AgentId,
    SessionId? SessionId,
    AgentBackendKind BackendKind,
    string TargetId,
    AgentLifecycleState State,
    string? Summary = null,
    bool Completed = false,
    string? Output = null,
    string? Error = null,
    long UpdatedAt = 0);

public interface IAgentExecutionHandle : IAsyncDisposable
{
    AgentId AgentId { get; }
    SessionId? SessionId { get; }
    AgentBackendKind BackendKind { get; }
    string TargetId { get; }

    Task<AgentExecutionSnapshot> SendAsync(
        string message,
        AgentMessageIntent intent = AgentMessageIntent.Task,
        MessagePrincipal? sender = null,
        CancellationToken cancellationToken = default);

    Task<AgentExecutionSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);
    Task CancelAsync(CancellationToken cancellationToken = default);
}

public interface IAgentExecutionBackend
{
    AgentBackendKind Kind { get; }

    Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken = default);
    bool CanHandle(AgentDispatchRequest request);
    Task<IAgentExecutionHandle> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default);
    Task<IAgentExecutionHandle?> AttachAsync(AgentId agentId, string? targetId = null, CancellationToken cancellationToken = default);
}

public interface IAgentExecutionRouter
{
    const string ServiceName = "agentExecutionRouter";

    IDisposable RegisterBackend(IAgentExecutionBackend backend);
    Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken = default);
    Task<IAgentExecutionHandle> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default);
    Task<IAgentExecutionHandle?> AttachAsync(AgentId agentId, AgentBackendKind? preferredBackend = null, string? targetId = null, CancellationToken cancellationToken = default);
}
