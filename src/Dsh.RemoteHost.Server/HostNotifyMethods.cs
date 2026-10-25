using System.Text.Json;
using Dsh.Transport;
using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

/** host.notify.* requests against the host's notification history. */
internal static class HostNotifyMethods
{
    public static async ValueTask<JsonElement?> HandleAsync(HostNotificationCenter notifications, string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case NotificationsProtocol.MethodNotifyList:
            {
                var query = message.Params is { } element ? JsonSerializer.Deserialize(element, NotificationsProtocolJsonContext.Default.RemoteNotifyQuery) : null;
                return Serialize(await notifications.QueryAsync(query ?? new RemoteNotifyQuery(), cancellationToken).ConfigureAwait(false), NotificationsProtocolJsonContext.Default.RemoteNotifyPage);
            }
            case NotificationsProtocol.MethodNotifyMarkRead:
            {
                var request = Require(message, NotificationsProtocolJsonContext.Default.RemoteNotifyMarkRead, "markRead requires ids or all");
                return Serialize(await notifications.MarkReadAsync(request, cancellationToken).ConfigureAwait(false), NotificationsProtocolJsonContext.Default.Int32);
            }
            default:
                throw MethodNotFound(name);
        }
    }
}
