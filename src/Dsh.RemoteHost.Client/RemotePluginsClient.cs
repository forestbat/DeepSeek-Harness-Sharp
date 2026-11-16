using Dsh.RemoteHost.Rpc;

namespace Dsh.RemoteHost;

/** Client implementation of the optional Plugins domain. */
public sealed class RemotePluginsClient : IWorkspacePlugins
{
    private readonly IPluginsRpc _rpc;

    internal RemotePluginsClient(RemoteHostClient host, IPluginsRpc rpc) => _rpc = rpc;

    public Task<IReadOnlyList<RemotePluginInfo>> ListPluginsAsync(CancellationToken cancellationToken = default)
        => _rpc.ListAsync(cancellationToken);

    public Task<string> SetPluginPermissionAsync(string package, string permission, bool allowed, CancellationToken cancellationToken = default)
        => _rpc.SetPermissionAsync(new RemotePluginPermissionRequest(package, permission, allowed), cancellationToken);
}
