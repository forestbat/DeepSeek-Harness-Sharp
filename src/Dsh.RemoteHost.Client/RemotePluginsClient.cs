using static Dsh.RemoteHost.RemoteHostRpc;

namespace Dsh.RemoteHost;

/** Client implementation of the optional Plugins domain. */
public sealed class RemotePluginsClient
{
    private readonly RemoteHostClient _host;

    internal RemotePluginsClient(RemoteHostClient host) => _host = host;

    public Task<IReadOnlyList<RemotePluginInfo>> ListPluginsAsync(CancellationToken cancellationToken = default)
        => _host.SendAsync(PluginsProtocol.MethodPluginsList, null, PluginsProtocolJsonContext.Default.IReadOnlyListRemotePluginInfo, cancellationToken);

    public Task<string> SetPluginPermissionAsync(string package, string permission, bool allowed, CancellationToken cancellationToken = default)
        => _host.SendAsync(PluginsProtocol.MethodPluginPermission, Serialize(new RemotePluginPermissionRequest(package, permission, allowed), PluginsProtocolJsonContext.Default.RemotePluginPermissionRequest), PluginsProtocolJsonContext.Default.String, cancellationToken);
}



