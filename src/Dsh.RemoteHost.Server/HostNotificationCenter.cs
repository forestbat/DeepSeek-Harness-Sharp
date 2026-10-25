using System.Threading.Channels;
using Dsh.Notifications;

namespace Dsh.RemoteHost;

/**
 * The machine's notification history (host.notify.*): the host is its only writer, every client reads it and shares one read state.
 * Sources are pending decisions, registry exit transitions, and structured events through Report.
 * Appends go through one queue, so producers that run under other locks only hand the record off.
 */
public sealed class HostNotificationCenter : IAsyncDisposable
{
    public const string FileName = "notifications.json";

    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 500;
    private const int SnapshotLimit = 200;

    private readonly NotificationHistory _history;
    private readonly HostSessionRegistry _sessions;
    private readonly HostPendingDecisions _pending;
    private readonly HostBroadcast _broadcast = new();
    private readonly Channel<NotificationRecord> _queue = Channel.CreateUnbounded<NotificationRecord>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _updates = new(1, 1);
    private readonly Task _writer;

    private HostNotificationCenter(NotificationHistory history, string machineId, HostSessionRegistry sessions, HostPendingDecisions pending)
    {
        _history = history;
        MachineId = machineId;
        _sessions = sessions;
        _pending = pending;
        sessions.SessionExited += OnSessionExited;
        pending.Asked += OnAsked;
        _writer = WriteLoopAsync();
    }

    /** MachineId on every record: the host's machine name, so clients aggregating several hosts can tell them apart. */
    public string MachineId { get; }

    public static async Task<HostNotificationCenter> OpenAsync(string path, HostSessionRegistry sessions, HostPendingDecisions pending, CancellationToken cancellationToken)
    {
        var history = await NotificationHistory.OpenAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new HostNotificationCenter(history, Environment.MachineName, sessions, pending);
    }

    /** Snapshot and later changes share the mutation gate, so no client repairs a query/push race. */
    public async Task<HostSubscription> SubscribeAsync(CancellationToken cancellationToken)
    {
        await _updates.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _broadcast.Subscribe([Change()]);
        }
        finally
        {
            _updates.Release();
        }
    }

    /** List items and counters share one committed state with appends, read marks, and push snapshots. */
    public async Task<RemoteNotifyPage> QueryAsync(RemoteNotifyQuery query, CancellationToken cancellationToken)
    {
        await _updates.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return QuerySnapshot(query);
        }
        finally
        {
            _updates.Release();
        }
    }

    /** Marks notifications read and tells every client which ones changed; returns how many changed. */
    public async Task<int> MarkReadAsync(RemoteNotifyMarkRead request, CancellationToken cancellationToken)
    {
        await _updates.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var changed = request.All
                ? await _history.MarkAllReadAsync(new NotificationFilter(SessionId: request.SessionId), cancellationToken).ConfigureAwait(false)
                : await _history.MarkReadAsync(request.Ids ?? [], cancellationToken).ConfigureAwait(false);
            if (changed.Count > 0)
                _broadcast.Publish(Change(readIds: changed));
            return changed.Count;
        }
        finally
        {
            _updates.Release();
        }
    }

    /** Queues a notification; Id defaults to a fresh one (pass a stable id to make repeats of the same event collapse). */
    public void Report(NotificationKind kind, string? sessionId, string title, string? body = null, NotificationTarget? target = null, string? id = null)
        => _queue.Writer.TryWrite(new NotificationRecord(
            id ?? Guid.NewGuid().ToString("N"),
            MachineId,
            sessionId,
            kind,
            title,
            body,
            DateTimeOffset.UtcNow,
            target ?? new NotificationTarget(NotificationView.Session)));

    /** A display name for a session in notification titles. */
    public string Label(string sessionId)
        => _sessions.Get(sessionId) is { } session ? session.Title ?? session.AgentKind ?? session.SessionId : sessionId;

    private void OnAsked(HostPendingAsked asked)
    {
        var (kind, verb) = asked.Kind == PendingKinds.Approval
            ? (NotificationKind.ApprovalRequest, "needs approval")
            : (NotificationKind.Question, "asks a question");
        Report(kind, asked.SessionId, $"{Label(asked.SessionId)} {verb}", asked.Summary, new NotificationTarget(NotificationView.PendingRequest, asked.RequestId), asked.RequestId);
    }

    /** The registry emits only an actual live-to-exited transition. */
    private void OnSessionExited(RemoteSessionSummary session)
    {
        var name = session.Title ?? session.AgentKind ?? session.SessionId;
        Report(NotificationKind.SessionExited, session.SessionId, $"{name} exited", session.ExitCode is { } code ? $"exit code {code}" : null);
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var record in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await _updates.WaitAsync().ConfigureAwait(false);
            try
            {
                if (await _history.AppendAsync(record).ConfigureAwait(false))
                    _broadcast.Publish(Change(added: record));
            }
            finally
            {
                _updates.Release();
            }
        }
    }

    /** The caller holds _updates; push construction must not acquire the non-reentrant gate again. */
    private RemoteNotifyPage QuerySnapshot(RemoteNotifyQuery query)
    {
        var kinds = query.Kinds is { Count: > 0 } wanted ? wanted.ToHashSet() : null;
        var matching = _history.Query(new NotificationFilter(SessionId: query.SessionId, UnreadOnly: query.UnreadOnly))
            .Where(record => kinds?.Contains(record.Kind) != false)
            .ToList();
        var size = Math.Clamp(query.Limit ?? DefaultPageSize, 1, MaxPageSize);
        return new RemoteNotifyPage([.. matching.Skip(Math.Max(query.Offset, 0)).Take(size)], matching.Count, _history.CountUnread());
    }

    private HostNotification Change(NotificationRecord? added = null, IReadOnlyList<string>? readIds = null)
        => HostNotification.Create(NotificationsProtocol.NotificationNotifyChanged,
            new RemoteNotificationChange(QuerySnapshot(new RemoteNotifyQuery(Limit: SnapshotLimit)), added, readIds),
            NotificationsProtocolJsonContext.Default.RemoteNotificationChange);

    /** Stops listening first, so sessions ended by the host's own shutdown do not land in the history. */
    public async ValueTask DisposeAsync()
    {
        _sessions.SessionExited -= OnSessionExited;
        _pending.Asked -= OnAsked;
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
        _updates.Dispose();
    }
}

