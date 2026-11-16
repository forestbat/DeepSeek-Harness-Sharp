using Dsh.RemoteHost.Rpc;

namespace Dsh.RemoteHost;

/** host.agent.* requests, forwarded to the backend that owns the agent processes. */
internal sealed class HostAgentMethods(IRemoteAgentBackend backend, Action ensureAuthenticated) : IAgentRpc
{
    public async Task<IReadOnlyList<RemoteAgentCatalogEntry>> CatalogAsync(CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        return await backend.AgentCatalogAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteSessionSummary> StartAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        return await backend.StartAgentAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> PromptAsync(RemoteAgentPrompt request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        await backend.PromptAgentAsync(request.SessionId, request.Text, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> CancelAsync(RemoteSessionRef request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        await backend.CancelAgentAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> StopAsync(RemoteSessionRef request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        await backend.StopAgentAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<RemoteSessionSummary> ResumeAsync(RemoteSessionRef request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        return await backend.ResumeAgentAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
    }
}
