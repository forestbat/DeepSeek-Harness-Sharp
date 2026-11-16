using Dsh.RemoteHost;

namespace Dsh.Android.Services;

/** UI 眼里的"一台可连的机器": 远端链路与手机内嵌 host 都实现这个表面。 */
public interface IMachineLink : IAsyncDisposable
{
    string Name { get; }

    string Target { get; }

    MobileConnectionState State { get; }

    string? Detail { get; }

    HostInfo? Info { get; }

    IReadOnlyList<RemoteSessionSummary> Sessions { get; }

    RemotePendingList Pending { get; }

    event Action? Changed;

    void Start();

    Task<HostLease> WaitConnectedAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<RemoteSessionEvent> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken);
}
