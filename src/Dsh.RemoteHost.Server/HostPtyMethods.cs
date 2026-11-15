using Dsh.RemoteHost.Rpc;

namespace Dsh.RemoteHost;

/** Connection-local PTY RPC adapter. The connection owns subscription pumps, not PTY lifetimes. */
internal sealed class HostPtyMethods(
    IRemotePtyBackend backend,
    Action ensureAuthenticated,
    Func<string, string, Task> attach,
    Action<string> detach) : IPtyRpc
{
    public async Task<RemotePtyRef> StartAsync(RemotePtyStartRequest request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        return new RemotePtyRef(await backend.StartPtyAsync(request, cancellationToken).ConfigureAwait(false));
    }

    public async Task<bool> WriteAsync(RemotePtyInput request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        await backend.WritePtyAsync(request.PtyId, Convert.FromBase64String(request.Base64), cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> StopAsync(RemotePtyRef request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        await backend.StopPtyAsync(request.PtyId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> AttachAsync(RemotePtyAttachRequest request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        await attach(request.SubscriptionId, request.PtyId).ConfigureAwait(false);
        return true;
    }

    public Task<bool> DetachAsync(RemoteSubscriptionRef request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        detach(request.SubscriptionId);
        return Task.FromResult(true);
    }

    public async Task<bool> ResizeAsync(RemotePtyResize request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        await backend.ResizePtyAsync(request.PtyId, request.Columns, request.Rows, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
