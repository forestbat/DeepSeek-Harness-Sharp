using System.Threading.Channels;
using Dsh.RemoteHost.Rpc;

namespace Dsh.RemoteHost;

/** Client implementation of the optional Notifications domain. */
public sealed class RemoteNotificationsClient : IWorkspaceNotifications
{
    private readonly INotificationsRpc _rpc;

    internal RemoteNotificationsClient(RemoteHostClient host, INotificationsRpc rpc) => _rpc = rpc;

    private readonly Channel<RemoteNotificationChange> _notificationChanges = Channel.CreateUnbounded<RemoteNotificationChange>(new UnboundedChannelOptions { SingleReader = true });

    public Task<RemoteNotifyPage> ListNotificationsAsync(RemoteNotifyQuery? query = null, CancellationToken cancellationToken = default)
        => _rpc.ListAsync(query ?? new RemoteNotifyQuery(), cancellationToken);

    public Task<int> MarkNotificationsReadAsync(RemoteNotifyMarkRead request, CancellationToken cancellationToken = default)
        => _rpc.MarkReadAsync(request, cancellationToken);

    public IAsyncEnumerable<RemoteNotificationChange> NotificationChangesAsync(CancellationToken cancellationToken = default)
        => _notificationChanges.Reader.ReadAllAsync(cancellationToken);

    internal Task OnChangedAsync(RemoteNotificationChange change)
    {
        _notificationChanges.Writer.TryWrite(change);
        return Task.CompletedTask;
    }

    internal void OnClosed(Exception failure)
    {
        _notificationChanges.Writer.TryComplete(failure);
    }
}
