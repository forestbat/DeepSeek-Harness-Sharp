using System.Text.Json;
using static Dsh.RemoteHost.RemoteHostRpc;
using System.Threading.Channels;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/** Client implementation of the optional Notifications domain. */
public sealed class RemoteNotificationsClient
{
    private readonly RemoteHostClient _host;

    internal RemoteNotificationsClient(RemoteHostClient host) => _host = host;

    private readonly Channel<RemoteNotificationChange> _notificationChanges = Channel.CreateUnbounded<RemoteNotificationChange>(new UnboundedChannelOptions { SingleReader = true });

    public Task<RemoteNotifyPage> ListNotificationsAsync(RemoteNotifyQuery? query = null, CancellationToken cancellationToken = default)
        => _host.SendAsync(NotificationsProtocol.MethodNotifyList, Serialize(query ?? new RemoteNotifyQuery(), NotificationsProtocolJsonContext.Default.RemoteNotifyQuery), NotificationsProtocolJsonContext.Default.RemoteNotifyPage, cancellationToken);

    public Task<int> MarkNotificationsReadAsync(RemoteNotifyMarkRead request, CancellationToken cancellationToken = default)
        => _host.SendAsync(NotificationsProtocol.MethodNotifyMarkRead, Serialize(request, NotificationsProtocolJsonContext.Default.RemoteNotifyMarkRead), NotificationsProtocolJsonContext.Default.Int32, cancellationToken);

    public IAsyncEnumerable<RemoteNotificationChange> NotificationChangesAsync(CancellationToken cancellationToken = default)
        => _notificationChanges.Reader.ReadAllAsync(cancellationToken);

    internal void OnNotification(JsonRpcMessage message)
    {
        if (message.Method == NotificationsProtocol.NotificationNotifyChanged && message.Params is { } parameters
            && JsonSerializer.Deserialize(parameters, NotificationsProtocolJsonContext.Default.RemoteNotificationChange) is { } changed)
            _notificationChanges.Writer.TryWrite(changed);
    }

    internal void OnClosed(Exception failure)
    {
        _notificationChanges.Writer.TryComplete(failure);
    }
}



