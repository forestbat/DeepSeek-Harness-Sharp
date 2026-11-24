using Dsh.RemoteHost.Rpc;

namespace Dsh.RemoteHost;

internal sealed class HostSessionEditingMethods(IRemoteSessionEditingBackend backend, Action ensureAuthenticated) : ISessionEditingRpc
{
    public async Task<RemoteCommandResult> ExecuteCommandAsync(RemoteCommandRequest request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        return await backend.ExecuteSessionCommandAsync(request.SessionId, request.Line, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteCommandDescriptor>> ListCommandsAsync(RemoteCommandRequest request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        return await backend.ListSessionCommandsAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RenameAsync(RemoteSessionRenameRequest request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        await backend.RenameSessionAsync(request.SessionId, request.Title, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DeleteAsync(RemoteSessionRef request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        await backend.DeleteSessionAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> SetApprovalPolicyAsync(RemoteSessionPolicyRequest request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        await backend.SetSessionApprovalPolicyAsync(request.SessionId, request.Policy, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<RemoteTimelineEntry>> SessionTimelineAsync(RemoteSessionRef request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        return await backend.ListSessionTimelineAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteSubagentInfo>> ListSubagentsAsync(RemoteSessionRef request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        return await backend.ListSubagentsAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
    }
}
