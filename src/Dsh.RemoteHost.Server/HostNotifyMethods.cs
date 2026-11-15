using Dsh.RemoteHost.Rpc;

namespace Dsh.RemoteHost;

/** host.notify.* requests against the host's notification history. */
internal sealed class HostNotifyMethods(HostNotificationCenter notifications, Action ensureAuthenticated) : INotificationsRpc
{
    public async Task<RemoteNotifyPage> ListAsync(RemoteNotifyQuery? request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        return await notifications.QueryAsync(request ?? new RemoteNotifyQuery(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> MarkReadAsync(RemoteNotifyMarkRead request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        return await notifications.MarkReadAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
