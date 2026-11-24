using Dsh.RemoteHost.Rpc;

namespace Dsh.RemoteHost;

/** Client implementation of the optional SessionEditing domain. */
public sealed class RemoteSessionEditingClient : IWorkspaceSessionEditing
{
    private readonly ISessionEditingRpc _rpc;

    internal RemoteSessionEditingClient(RemoteHostClient host, ISessionEditingRpc rpc) => _rpc = rpc;

    public Task<RemoteCommandResult> ExecuteSessionCommandAsync(string sessionId, string line, CancellationToken cancellationToken = default)
        => _rpc.ExecuteCommandAsync(new RemoteCommandRequest(sessionId, line), cancellationToken);

    public Task<IReadOnlyList<RemoteCommandDescriptor>> ListSessionCommandsAsync(string sessionId, CancellationToken cancellationToken = default)
        => _rpc.ListCommandsAsync(new RemoteCommandRequest(sessionId, string.Empty), cancellationToken);

    public async Task RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken = default)
        => await _rpc.RenameAsync(new RemoteSessionRenameRequest(sessionId, title), cancellationToken).ConfigureAwait(false);

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => await _rpc.DeleteAsync(new RemoteSessionRef(sessionId), cancellationToken).ConfigureAwait(false);

    public async Task SetSessionApprovalPolicyAsync(string sessionId, string policy, CancellationToken cancellationToken = default)
        => await _rpc.SetApprovalPolicyAsync(new RemoteSessionPolicyRequest(sessionId, policy), cancellationToken).ConfigureAwait(false);

    public Task<IReadOnlyList<RemoteTimelineEntry>> SessionTimelineAsync(string sessionId, CancellationToken cancellationToken = default)
        => _rpc.SessionTimelineAsync(new RemoteSessionRef(sessionId), cancellationToken);

    public Task<IReadOnlyList<RemoteSubagentInfo>> ListSubagentsAsync(string rootSessionId, CancellationToken cancellationToken = default)
        => _rpc.ListSubagentsAsync(new RemoteSessionRef(rootSessionId), cancellationToken);
}
