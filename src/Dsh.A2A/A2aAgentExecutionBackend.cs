using System.Collections.Concurrent;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.A2A;

public sealed class A2aAgentExecutionBackend : IAgentExecutionBackend
{
    public const string TargetPrefix = "a2a:";
    private const int DefaultTimeoutSeconds = 120;

    private readonly IReadOnlyDictionary<string, A2aRemoteSettings> _remotes;
    private readonly A2aRemoteClient _client;
    private readonly ConcurrentDictionary<AgentId, A2aExecutionHandle> _handles = new();

    public A2aAgentExecutionBackend(
        IReadOnlyDictionary<string, A2aRemoteSettings> remotes,
        OutboundProxy? proxy = null)
        : this(remotes, new A2aRemoteClient(remotes, proxy))
    {
    }

    public A2aAgentExecutionBackend(
        IReadOnlyDictionary<string, A2aRemoteSettings> remotes,
        A2aRemoteClient client)
    {
        _remotes = remotes;
        _client = client;
    }

    public AgentBackendKind Kind => AgentBackendKind.A2a;

    public Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var targets = _remotes.Select(entry => new RemoteExecutionTarget(
            TargetId: $"{TargetPrefix}{entry.Key}",
            Kind: AgentBackendKind.A2a,
            DisplayName: entry.Key,
            Endpoint: entry.Value.Url,
            Available: !string.IsNullOrWhiteSpace(entry.Value.Url),
            Capabilities: ["a2a"])).ToList();
        return Task.FromResult<IReadOnlyList<RemoteExecutionTarget>>(targets);
    }

    public bool CanHandle(AgentDispatchRequest request)
    {
        if (request.TargetId is { Length: > 0 } targetId)
            return targetId.StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase);
        return request.PreferredBackend == AgentBackendKind.A2a;
    }

    public async Task<IAgentExecutionHandle> DispatchAsync(
        AgentDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        var (remoteTarget, targetId) = ResolveRemoteTarget(request);
        var contextId = request.SessionId?.Value ?? request.AgentId?.Value;
        var prompt = request.Prompt ?? "";
        var result = await _client.SendAsync(
            remoteTarget,
            prompt,
            request.WaitForCompletion,
            DefaultTimeoutSeconds,
            contextId,
            cancellationToken).ConfigureAwait(false);

        var resolvedContextId = result.Task?.ContextId is { Length: > 0 } ctxId ? ctxId : contextId;
        var agentId = request.AgentId ?? AgentId.Create(resolvedContextId ?? result.Task?.Id ?? Guid.NewGuid().ToString("N"));
        var sessionId = resolvedContextId is { Length: > 0 } ? SessionId.Create(resolvedContextId) : (SessionId?)null;
        var handle = new A2aExecutionHandle(_client, remoteTarget, targetId, agentId, sessionId, resolvedContextId, result);
        _handles[agentId] = handle;
        return handle;
    }

    public async Task<IAgentExecutionHandle?> AttachAsync(
        AgentId agentId,
        string? targetId = null,
        CancellationToken cancellationToken = default)
    {
        if (_handles.TryGetValue(agentId, out var existing))
            return existing;
        if (string.IsNullOrWhiteSpace(targetId) || !targetId.StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var remoteTarget = targetId[TargetPrefix.Length..];
        if (remoteTarget.Length == 0)
            return null;
        try
        {
            var tasks = await _client.ListAsync(remoteTarget, agentId.Value, 10, cancellationToken).ConfigureAwait(false);
            var latest = tasks.Tasks.LastOrDefault();
            var seedResult = latest is null ? null : new A2aTaskResult(true, latest, latest.Answer, latest.Error);
            var handle = new A2aExecutionHandle(
                _client,
                remoteTarget,
                targetId,
                agentId,
                SessionId.Create(agentId.Value),
                agentId.Value,
                seedResult);
            _handles[agentId] = handle;
            return handle;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    private (string RemoteTarget, string TargetId) ResolveRemoteTarget(AgentDispatchRequest request)
    {
        if (request.TargetId is { Length: > 0 } targetId)
        {
            if (!targetId.StartsWith(TargetPrefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Invalid A2A target id \"{targetId}\".", nameof(request));
            var remote = targetId[TargetPrefix.Length..];
            if (remote.Length == 0)
                throw new ArgumentException("A2A target id must specify a remote name or URL.", nameof(request));
            return (remote, targetId);
        }
        var first = _remotes.Keys.FirstOrDefault()
            ?? throw new InvalidOperationException("No A2A remotes are configured.");
        return (first, $"{TargetPrefix}{first}");
    }

    private sealed class A2aExecutionHandle(
        A2aRemoteClient client,
        string remoteTarget,
        string targetId,
        AgentId agentId,
        SessionId? sessionId,
        string? contextId,
        A2aTaskResult? initialResult) : IAgentExecutionHandle
    {
        private string? _contextId = contextId;
        private string? _lastTaskId = initialResult?.Task?.Id;
        private AgentExecutionSnapshot _latest = ToSnapshot(agentId, sessionId, targetId, initialResult);

        public AgentId AgentId { get; } = agentId;
        public SessionId? SessionId { get; private set; } = sessionId;
        public AgentBackendKind BackendKind => AgentBackendKind.A2a;
        public string TargetId { get; } = targetId;

        public async Task<AgentExecutionSnapshot> SendAsync(
            string message,
            AgentMessageIntent intent = AgentMessageIntent.Task,
            MessagePrincipal? sender = null,
            CancellationToken cancellationToken = default)
        {
            var result = await client.SendAsync(
                remoteTarget,
                message,
                wait: true,
                DefaultTimeoutSeconds,
                _contextId,
                cancellationToken).ConfigureAwait(false);
            UpdateFromResult(result);
            return _latest;
        }

        public async Task<AgentExecutionSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_lastTaskId))
                return _latest;
            var result = await client.GetAsync(remoteTarget, _lastTaskId, cancellationToken).ConfigureAwait(false);
            UpdateFromResult(result);
            return _latest;
        }

        public async Task CancelAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_lastTaskId))
                return;
            var result = await client.CancelAsync(remoteTarget, _lastTaskId, cancellationToken).ConfigureAwait(false);
            UpdateFromResult(result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void UpdateFromResult(A2aTaskResult result)
        {
            if (result.Task is { } task)
            {
                _lastTaskId = task.Id;
                if (!string.IsNullOrEmpty(task.ContextId))
                {
                    _contextId = task.ContextId;
                    SessionId ??= Dsh.Llm.SessionId.Create(task.ContextId);
                }
            }
            _latest = ToSnapshot(AgentId, SessionId, TargetId, result);
        }

        private static AgentExecutionSnapshot ToSnapshot(
            AgentId agentId,
            SessionId? sessionId,
            string targetId,
            A2aTaskResult? result)
        {
            if (result is null)
            {
                return new AgentExecutionSnapshot(
                    agentId,
                    sessionId,
                    AgentBackendKind.A2a,
                    targetId,
                    AgentLifecycleState.Idle,
                    UpdatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }

            if (result.Task is not { } task)
            {
                return new AgentExecutionSnapshot(
                    agentId,
                    sessionId,
                    AgentBackendKind.A2a,
                    targetId,
                    AgentLifecycleState.Idle,
                    Summary: result.Answer ?? result.Error,
                    Completed: true,
                    Output: result.Answer,
                    Error: result.Error,
                    UpdatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }

            var terminal = task.State is "Completed" or "Failed" or "Canceled" or "Rejected";
            var state = task.State switch
            {
                "Canceled" => AgentLifecycleState.Disposed,
                "Completed" or "Failed" or "Rejected" => AgentLifecycleState.Idle,
                _ => AgentLifecycleState.Active,
            };
            var output = task.Answer ?? result.Answer;
            var error = task.Error ?? result.Error;
            return new AgentExecutionSnapshot(
                agentId,
                sessionId,
                AgentBackendKind.A2a,
                targetId,
                state,
                Summary: output ?? error,
                Completed: terminal,
                Output: output,
                Error: error,
                UpdatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }
}
