using Dsh.RemoteHost.Rpc;

namespace Dsh.RemoteHost;

internal sealed class HostPluginsMethods(IRemotePluginsBackend backend, Action ensureAuthenticated) : IPluginsRpc
{
    public async Task<IReadOnlyList<RemotePluginInfo>> ListAsync(CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        return await backend.ListPluginsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> SetPermissionAsync(RemotePluginPermissionRequest request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        return await backend.SetPluginPermissionAsync(request.Package, request.Permission, request.Allowed, cancellationToken).ConfigureAwait(false);
    }
}
