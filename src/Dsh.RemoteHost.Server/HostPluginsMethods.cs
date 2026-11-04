using System.Text.Json;
using Dsh.Transport;
using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

internal static class HostPluginsMethods
{
    public static async ValueTask<JsonElement?> HandleAsync(IRemotePluginsBackend backend, string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case PluginsProtocol.MethodPluginsList:
                return Serialize(await backend.ListPluginsAsync(cancellationToken).ConfigureAwait(false), PluginsProtocolJsonContext.Default.IReadOnlyListRemotePluginInfo);
            case PluginsProtocol.MethodPluginPermission:
            {
                var request = Require(message, PluginsProtocolJsonContext.Default.RemotePluginPermissionRequest, "permission requires package, permission and allowed");
                return Serialize(await backend.SetPluginPermissionAsync(request.Package, request.Permission, request.Allowed, cancellationToken).ConfigureAwait(false), PluginsProtocolJsonContext.Default.String);
            }
            default:
                throw MethodNotFound(name);
        }
    }
}


