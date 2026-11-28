using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Agent;

/** Dispatches and attaches agents across local and registered remote execution backends. */
public sealed class AgentExecutionRouter : Service, IAgentExecutionRouter
{
    private readonly Lock _gate = new();
    private readonly List<IAgentExecutionBackend> _backends = [];

    public AgentExecutionRouter(Context ctx) : base(ctx, IAgentExecutionRouter.ServiceName)
    {
        _ = RegisterBackend(new LocalAgentExecutionBackend(ctx));
    }

    public IDisposable RegisterBackend(IAgentExecutionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        lock (_gate)
            _backends.Add(backend);
        return new BackendRegistration(() =>
        {
            lock (_gate)
                _backends.Remove(backend);
        });
    }

    public async Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken = default)
    {
        var backends = SnapshotBackends();
        var targets = new List<RemoteExecutionTarget>();
        foreach (var backend in backends)
        {
            var batch = await backend.ListTargetsAsync(cancellationToken).ConfigureAwait(false);
            targets.AddRange(batch);
        }
        return targets;
    }

    public async Task<IAgentExecutionHandle> DispatchAsync(AgentDispatchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var backends = SnapshotBackends();
        for (var index = backends.Count - 1; index >= 0; index--)
        {
            var backend = backends[index];
            if (request.PreferredBackend is { } preferred && backend.Kind != preferred)
                continue;
            if (backend.CanHandle(request))
                return await backend.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("no registered execution backend can handle the agent dispatch request");
    }

    public async Task<IAgentExecutionHandle?> AttachAsync(
        AgentId agentId,
        AgentBackendKind? preferredBackend = null,
        string? targetId = null,
        CancellationToken cancellationToken = default)
    {
        var backends = SnapshotBackends();
        for (var index = backends.Count - 1; index >= 0; index--)
        {
            var backend = backends[index];
            if (preferredBackend is { } preferred && backend.Kind != preferred)
                continue;
            if (await backend.AttachAsync(agentId, targetId, cancellationToken).ConfigureAwait(false) is { } handle)
                return handle;
        }
        return null;
    }

    private List<IAgentExecutionBackend> SnapshotBackends()
    {
        lock (_gate)
            return [.. _backends];
    }

    private sealed class BackendRegistration(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                dispose();
        }
    }
}
