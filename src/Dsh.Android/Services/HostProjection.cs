using Dsh.RemoteHost;
using Dsh.Workspace;

namespace Dsh.Android.Services;

/**
 * 一条链路上的两份权威投影: 会话登记表与待决请求。
 * 远端链路与手机内嵌 host 都靠它把推送收敛成 UI 需要的形状。
 */
public sealed class HostProjection
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RemoteSessionSummary> _sessions = new(StringComparer.Ordinal);
    private RemotePendingList _pending = new([], []);

    public IReadOnlyList<RemoteSessionSummary> Sessions
    {
        get
        {
            lock (_gate)
                return [.. _sessions.Values];
        }
    }

    public RemotePendingList Pending
    {
        get
        {
            lock (_gate)
                return _pending;
        }
    }

    public void ReplacePending(RemotePendingList pending)
    {
        lock (_gate)
            _pending = pending;
    }

    public void ApplySession(RemoteSessionChange change)
    {
        lock (_gate)
        {
            if (change.Snapshot is { } snapshot)
            {
                _sessions.Clear();
                foreach (var session in snapshot)
                    _sessions[session.SessionId] = session;
            }
            if (change.Session is { } updated)
            {
                if (updated.Deleted)
                    _sessions.Remove(updated.SessionId);
                else
                    _sessions[updated.SessionId] = updated;
            }
        }
    }

    /** 会话登记表与待决广播是长跑订阅: 连断开都算正常结束, 抛错只影响这一条链路。 */
    public Task[] PumpAsync(IWorkspaceHost client, Action changed, CancellationToken cancellationToken)
        => [Pump(client.PendingChangesAsync(cancellationToken), change => ReplacePending(change.Pending), changed),
            Pump(client.HostSessionChangesAsync(cancellationToken), ApplySession, changed)];

    private static async Task Pump<T>(IAsyncEnumerable<T> source, Action<T> apply, Action changed)
    {
        try
        {
            await foreach (var item in source.ConfigureAwait(false))
            {
                apply(item);
                changed();
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }
}
