using Dsh.RemoteHost.Rpc;

namespace Dsh.RemoteHost;

/** Client implementation of the optional Agent domain. */
public sealed class RemoteAgentClient : IWorkspaceAgents
{
    private readonly IAgentRpc _rpc;

    internal RemoteAgentClient(RemoteHostClient host, IAgentRpc rpc) => _rpc = rpc;

    public Task<IReadOnlyList<RemoteAgentCatalogEntry>> AgentCatalogAsync(CancellationToken cancellationToken = default)
        => _rpc.CatalogAsync(cancellationToken);

    public Task<RemoteSessionSummary> StartAgentAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken = default)
        => _rpc.StartAsync(request, cancellationToken);

    public async Task PromptAgentAsync(string sessionId, string text, CancellationToken cancellationToken = default)
        => await _rpc.PromptAsync(new RemoteAgentPrompt(sessionId, text), cancellationToken).ConfigureAwait(false);

    public async Task CancelAgentAsync(string sessionId, CancellationToken cancellationToken = default)
        => await _rpc.CancelAsync(new RemoteSessionRef(sessionId), cancellationToken).ConfigureAwait(false);

    public async Task StopAgentAsync(string sessionId, CancellationToken cancellationToken = default)
        => await _rpc.StopAsync(new RemoteSessionRef(sessionId), cancellationToken).ConfigureAwait(false);

    public Task<RemoteSessionSummary> ResumeAgentAsync(string sessionId, CancellationToken cancellationToken = default)
        => _rpc.ResumeAsync(new RemoteSessionRef(sessionId), cancellationToken);
}
