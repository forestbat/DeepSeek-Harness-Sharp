using System.Text.Json;
using static Dsh.RemoteHost.RemoteHostRpc;

namespace Dsh.RemoteHost;

/** Client implementation of the optional Agent domain. */
public sealed class RemoteAgentClient : IWorkspaceAgents
{
    private readonly RemoteHostClient _host;

    internal RemoteAgentClient(RemoteHostClient host) => _host = host;

    public Task<IReadOnlyList<RemoteAgentCatalogEntry>> AgentCatalogAsync(CancellationToken cancellationToken = default)
        => _host.SendAsync(AgentProtocol.MethodAgentCatalog, null, AgentProtocolJsonContext.Default.IReadOnlyListRemoteAgentCatalogEntry, cancellationToken);

    public Task<RemoteSessionSummary> StartAgentAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken = default)
        => _host.SendAsync(AgentProtocol.MethodAgentStart, Serialize(request, AgentProtocolJsonContext.Default.RemoteAgentStartRequest), AgentProtocolJsonContext.Default.RemoteSessionSummary, cancellationToken);

    public Task PromptAgentAsync(string sessionId, string text, CancellationToken cancellationToken = default)
        => _host.SendAsync(AgentProtocol.MethodAgentPrompt, Serialize(new RemoteAgentPrompt(sessionId, text), AgentProtocolJsonContext.Default.RemoteAgentPrompt), AgentProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task CancelAgentAsync(string sessionId, CancellationToken cancellationToken = default)
        => _host.SendAsync(AgentProtocol.MethodAgentCancel, SessionRef(sessionId), AgentProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task StopAgentAsync(string sessionId, CancellationToken cancellationToken = default)
        => _host.SendAsync(AgentProtocol.MethodAgentStop, SessionRef(sessionId), AgentProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task<RemoteSessionSummary> ResumeAgentAsync(string sessionId, CancellationToken cancellationToken = default)
        => _host.SendAsync(AgentProtocol.MethodAgentResume, SessionRef(sessionId), AgentProtocolJsonContext.Default.RemoteSessionSummary, cancellationToken);

    private static JsonElement SessionRef(string sessionId) => Serialize(new RemoteSessionRef(sessionId), AgentProtocolJsonContext.Default.RemoteSessionRef);
}




