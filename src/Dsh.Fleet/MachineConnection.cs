using System.Diagnostics;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Fleet;

/**
 * The single connection a client keeps to one machine; every session on that machine shares it.
 * A background loop connects (deploying first when needed), keeps the link alive with a heartbeat, and reconnects with exponential backoff after a drop.
 * Each connection starts with ordered Host snapshots, then applies their changes without another query or replay cache.
 * The last authoritative projections remain visible while the machine is not connected.
 */
public sealed class MachineConnection : IAsyncDisposable
{
    private readonly FleetOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RemoteSessionSummary> _sessions = new(StringComparer.Ordinal);
    private RemotePendingList _pending = new([], []);
    private RemoteNotifyPage? _notifications;
    private readonly RemoteHostSubscriptions _subscriptions;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<HostLease> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _commandPath;
    private Task? _loop;
    private bool _forceRedeploy;
    private bool _autoRedeployUsed;

    public MachineConnection(MachineSpec machine, FleetOptions options)
    {
        Machine = machine;
        _options = options;
        _subscriptions = new RemoteHostSubscriptions(WaitConnectedAsync, _lifetime.Token);
    }

    public MachineSpec Machine { get; }

    public MachineConnectionState State { get; private set; } = MachineConnectionState.Offline;

    /** The last failure, or the deployment step in progress. */
    public string? Detail { get; private set; }

    public HostInfo? Info { get; private set; }

    public HostHelloResponse? Hello { get; private set; }

    /** The ssh process of the current link (SSH machines only). */
    internal Process? LinkProcess { get; private set; }

    public event Action<MachineConnection>? StateChanged;

    public event Action<MachineConnection>? SessionsChanged;

    public event Action<MachineConnection>? PendingChanged;

    public event Action<MachineConnection, RemoteNotificationChange?>? NotificationsChanged;

    public RemoteNotifyPage? Notifications
    {
        get
        {
            lock (_gate)
                return _notifications;
        }
    }

    public int UnreadNotifications => Notifications?.UnreadCount ?? 0;

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

    public void Start() => _loop ??= RunAsync(_lifetime.Token);

    /** Completes once the first attempt has connected, failed, or ended during disposal. */
    public Task WaitSettledAsync(CancellationToken cancellationToken) => _settled.Task.WaitAsync(cancellationToken);

    public Task<HostLease> WaitConnectedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            return _connected.Task.WaitAsync(cancellationToken);
    }

    /** Borrows only the currently published lease; never waits for a future reconnect. */
    internal HostLease? CurrentLease
    {
        get
        {
            lock (_gate)
                return _connected.Task.IsCompletedSuccessfully ? _connected.Task.Result : null;
        }
    }

    /** Captures the published connection and its Host identity under the same lock. */
    internal (HostLease Lease, HostInfo Info)? CurrentHostLease
    {
        get
        {
            lock (_gate)
                return _connected.Task.IsCompletedSuccessfully && Info is { } info
                    ? (_connected.Task.Result, info) : null;
        }
    }

    /** 每个读者使用共享 RemoteHost 续订语义，不保存另一份历史或去重记忆。 */
    public IAsyncEnumerable<RemoteSessionEvent> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
        => _subscriptions.SubscribeAsync(sessionId, fromSeq, cancellationToken);


    /**
     * Attaches to a PTY across reconnections: after a drop the next link attaches again and the host starts over with a full-screen snapshot.
     * Returns when the PTY ends; fails when the host does not know the PTY (for example it exited while the machine was unreachable).
     */
    public Task AttachPtyAsync(string ptyId, Stream input, Stream output, CancellationToken cancellationToken,
        Func<RemoteHostClient, CancellationToken, Task>? prepare = null)
        => _subscriptions.AttachPtyAsync(ptyId, input, output, cancellationToken, prepare);


    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_loop is { } loop)
                await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Disposing while an attempt is still connecting cancels it; that ended FleetClient.DisposeAsync before the other machines were released.
        }
        finally
        {
            _settled.TrySetResult();
            _lifetime.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        var everConnected = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            // A reconnect keeps showing why the previous link dropped until the new attempt settles.
            SetState(everConnected ? MachineConnectionState.Reconnecting : MachineConnectionState.Connecting, everConnected ? Detail : null);
            var link = await TryOpenAsync(cancellationToken).ConfigureAwait(false);
            if (link is null)
                failures++;
            else
            {
                failures = 0;
                await ServeAsync(link, cancellationToken).ConfigureAwait(false);
                everConnected |= State == MachineConnectionState.Connected;
            }

            SetState(_options.AutoReconnect && everConnected ? MachineConnectionState.Reconnecting : MachineConnectionState.Offline, Detail);
            _settled.TrySetResult();
            if (!_options.AutoReconnect || !await DelayAsync(_options.Backoff(failures), cancellationToken).ConfigureAwait(false))
                break;
        }

        SetState(MachineConnectionState.Offline, Detail);
    }

    private async Task<HostLink?> TryOpenAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Machine.IsLocal)
                return await HostLink.OpenLocalAsync(Machine, _options, cancellationToken).ConfigureAwait(false);
            var commandPath = await ResolveCommandPathAsync(cancellationToken).ConfigureAwait(false);
            return await HostLink.OpenSshAsync(Machine, commandPath, _options, cancellationToken).ConfigureAwait(false);
        }
        catch (HostProtocolMismatchException error)
        {
            _commandPath = null;
            if (error.RemoteIsNewer)
            {
                SetState(MachineConnectionState.Offline, $"客户端协议 v{error.LocalVersion} 过旧，远端为 v{error.RemoteVersion}；请升级本机 dsharp");
                return null;
            }
            if (_options.Deploy.AutoDeploy && !_autoRedeployUsed)
            {
                _autoRedeployUsed = true;
                return await RedeployAndOpenAsync(cancellationToken).ConfigureAwait(false);
            }
            SetState(MachineConnectionState.Offline, error.Message);
            return null;
        }
        // A cancelled attempt can also fail with its own error: an ssh killed by the cancellation exits with 255, which surfaced from DisposeAsync.
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The deployed path may be stale (removed or replaced); resolve it again on the next attempt.
            _commandPath = null;
            SetState(MachineConnectionState.Offline, error.Message);
            return null;
        }
    }

    private async Task<HostLink?> RedeployAndOpenAsync(CancellationToken cancellationToken)
    {
        try
        {
            _forceRedeploy = true;
            var commandPath = await ResolveCommandPathAsync(cancellationToken).ConfigureAwait(false);
            return await HostLink.OpenSshAsync(Machine, commandPath, _options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            SetState(MachineConnectionState.Offline, error.Message);
            return null;
        }
    }

    /** The remote dsharp to run: deploy or upgrade it when automatic deployment is on, otherwise use whatever the probe finds (null falls back to PATH or the remote directory). */
    private async Task<string?> ResolveCommandPathAsync(CancellationToken cancellationToken)
    {
        if (_commandPath is not null)
            return _commandPath;
        if (_options.Deploy.AutoDeploy)
        {
            var force = _forceRedeploy;
            _forceRedeploy = false;
            SetState(MachineConnectionState.Deploying, force ? "协议不兼容，正在重新部署…" : null);
            var deployed = await new RemoteDsharpInstaller(new SshTransport(Machine, _options.SshTransportAdapter), _options.SshTransportAdapter).EnsureAsync(
                _options.Deploy, step => SetState(MachineConnectionState.Deploying, step), cancellationToken, _options.ArchiveFactory, force,
                localProxy: _options.LocalProxy, localHome: _options.LocalHome).ConfigureAwait(false);
            return _commandPath = deployed.CommandPath;
        }

        try
        {
            return _commandPath = (await new RemoteDsharpInstaller(new SshTransport(Machine, _options.SshTransportAdapter), _options.SshTransportAdapter).InspectAsync(cancellationToken).ConfigureAwait(false)).CommandPath;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /** Publishes the lease only after all provided domains supplied their initial authoritative projection. */
    private async Task ServeAsync(HostLink link, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] pumps = [];
        try
        {
            Info = await link.Client.InfoAsync(lifetime.Token).ConfigureAwait(false);
            Hello = link.Client.Hello;
            var projections = StartPumps(link, lifetime.Token);
            pumps = projections.Pumps;
            var ended = Task.WhenAny(pumps);
            if (await Task.WhenAny(projections.Ready, ended).WaitAsync(lifetime.Token).ConfigureAwait(false) == ended)
            {
                await (await ended.ConfigureAwait(false)).ConfigureAwait(false);
                throw new IOException("host connection ended before its initial projection");
            }
            await projections.Ready.ConfigureAwait(false);
            Publish(new HostLease(link.Client, lifetime.Token, closed.Task), link.Process);
            await (await ended.ConfigureAwait(false)).ConfigureAwait(false);
            Detail = "connection lost";
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            Detail = error.Message;
        }
        finally
        {
            Unpublish();
            await lifetime.CancelAsync().ConfigureAwait(false);
            closed.TrySetResult();
            await link.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(pumps).ConfigureAwait(false);
        }
    }

    private (Task[] Pumps, Task Ready) StartPumps(HostLink link, CancellationToken cancellationToken)
    {
        var client = link.Client;
        if (!client.Supports(HostProtocol.SessionRegistryCapability) || !client.Supports(HostProtocol.PendingBroadcastCapability))
            throw new NotSupportedException("the Host does not provide core session and pending projections");
        var hasNotifications = client.Supports(NotificationsProtocol.Capability);
        var ready = new Task[hasNotifications ? 3 : 2];
        var pumps = new Task[ready.Length + 1];
        (pumps[0], ready[0]) = StartPump(client.PendingChangesAsync(cancellationToken), change => ReplacePending(change.Pending));
        (pumps[1], ready[1]) = StartPump(client.HostSessionChangesAsync(cancellationToken), ApplySession);
        if (hasNotifications)
            (pumps[2], ready[2]) = StartPump(client.Notifications.NotificationChangesAsync(cancellationToken), ApplyNotifications);
        else
            ApplyNotifications(null);
        pumps[^1] = WatchAsync(link, cancellationToken);
        return (pumps, Task.WhenAll(ready));
    }

    private static (Task Pump, Task Ready) StartPump<T>(IAsyncEnumerable<T> source, Action<T> apply)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return (PumpAsync(source, apply, ready), ready.Task);
    }

    private static async Task PumpAsync<T>(IAsyncEnumerable<T> source, Action<T> apply, TaskCompletionSource ready)
    {
        try
        {
            await foreach (var item in source.ConfigureAwait(false))
            {
                apply(item);
                ready.TrySetResult();
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
        {
            if (!ready.Task.IsCompleted)
                ready.TrySetException(error);
        }
        finally
        {
            if (!ready.Task.IsCompleted)
                ready.TrySetException(new EndOfStreamException("host projection ended before its initial snapshot"));
        }
    }

    /** Returns when the ssh process exits, a heartbeat ping fails, or the client is shutting down. */
    private async Task WatchAsync(HostLink link, CancellationToken cancellationToken)
    {
        var exited = link.Exited;
        while (!cancellationToken.IsCancellationRequested)
        {
            var tick = Task.Delay(_options.HeartbeatInterval, cancellationToken);
            if (await Task.WhenAny(tick, exited).ConfigureAwait(false) == exited || tick.IsCanceled)
                return;
            if (!await PingAsync(link.Client, cancellationToken).ConfigureAwait(false))
                return;
        }
    }

    private async Task<bool> PingAsync(RemoteHostClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.HeartbeatTimeout);
        try
        {
            await client.PingAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or JsonRpcException or ObjectDisposedException)
        {
            return false;
        }
    }

    private void Publish(HostLease lease, Process? process)
    {
        lock (_gate)
            _connected.TrySetResult(lease);
        LinkProcess = process;
        _autoRedeployUsed = false;
        SetState(MachineConnectionState.Connected, null);
        _settled.TrySetResult();
    }

    private void Unpublish()
    {
        lock (_gate)
        {
            if (_connected.Task.IsCompleted)
                _connected = new TaskCompletionSource<HostLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        LinkProcess = null;
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void SetState(MachineConnectionState state, string? detail)
    {
        State = state;
        Detail = detail;
        StateChanged?.Invoke(this);
    }

    private void ApplySession(RemoteSessionChange change)
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
        SessionsChanged?.Invoke(this);
    }

    private void ApplyNotifications(RemoteNotificationChange? change)
    {
        lock (_gate)
            _notifications = change?.Page;
        NotificationsChanged?.Invoke(this, change);
    }

    private void ReplacePending(RemotePendingList pending)
    {
        lock (_gate)
            _pending = pending;
        PendingChanged?.Invoke(this);
    }
}
