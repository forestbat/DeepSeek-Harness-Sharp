using System.Net.Sockets;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Android.Services;

public enum MobileConnectionState
{
    Offline,
    Connecting,
    Connected,
    Reconnecting,
}

/**
 * 手机到桌面 host 的一条 TCP 链路: 只负责连接、心跳与重连, 会话语义全部由 RemoteHostClient 提供。
 * 订阅复用 RemoteHostSubscriptions, 因此断线后的续订从最后一条事件之后继续, 不需要另存一份历史。
 * Android 没有系统 ssh 进程, P1 走局域网 TCP + 握手令牌; SSH 客户端与 TLS 在路线图里按需补。
 */
public sealed class MobileHostConnection : IAsyncDisposable
{
    private const int ConnectTimeoutSeconds = 8;
    private const int HeartbeatSeconds = 15;
    private const int HeartbeatTimeoutSeconds = 10;
    private const int MaxBackoffMilliseconds = 10_000;

    private readonly HostEndpoint _endpoint;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RemoteHostSession> _sessions = new(StringComparer.Ordinal);
    private readonly RemoteHostSubscriptions _subscriptions;
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource<HostLease> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RemotePendingList _pending = new([], []);
    private Task? _loop;

    public MobileHostConnection(HostEndpoint endpoint)
    {
        _endpoint = endpoint;
        _subscriptions = new RemoteHostSubscriptions(WaitConnectedAsync, _lifetime.Token);
    }

    public MobileConnectionState State { get; private set; } = MobileConnectionState.Offline;

    public string? Detail { get; private set; }

    public HostInfo? Info { get; private set; }

    public event Action? Changed;

    public IReadOnlyList<RemoteHostSession> Sessions
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

    public Task<HostLease> WaitConnectedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
            return _connected.Task.WaitAsync(cancellationToken);
    }

    public IAsyncEnumerable<RemoteEventInfo> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
        => _subscriptions.SubscribeAsync(sessionId, fromSeq, cancellationToken);

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
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        var everConnected = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            SetState(everConnected ? MobileConnectionState.Reconnecting : MobileConnectionState.Connecting, Detail);
            try
            {
                await ServeOnceAsync(cancellationToken).ConfigureAwait(false);
                everConnected = true;
                failures = 0;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                failures++;
                SetState(everConnected ? MobileConnectionState.Reconnecting : MobileConnectionState.Offline, error.Message);
            }

            if (!await DelayAsync(Backoff(failures), cancellationToken).ConfigureAwait(false))
                break;
        }

        SetState(MobileConnectionState.Offline, Detail);
    }

    /** 一条链路从建立到断开; 返回即代表该链路已结束, 由外层按退避重连。 */
    private async Task ServeOnceAsync(CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient { NoDelay = true };
        using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            attempt.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSeconds));
            await tcp.ConnectAsync(_endpoint.Host, _endpoint.Port, attempt.Token).ConfigureAwait(false);
        }

        Stream stream = tcp.GetStream();
        await using (stream)
        await using (var client = await RemoteHostClient.ConnectAsync(stream, _endpoint.Token, "dsh-android", cancellationToken).ConfigureAwait(false))
        {
            Info = await client.InfoAsync(cancellationToken).ConfigureAwait(false);
            using var link = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pumps = StartProjections(client, link.Token);
            Publish(new HostLease(client, link.Token, closed.Task));
            SetState(MobileConnectionState.Connected, null);
            var watch = WatchAsync(client, link.Token);
            try
            {
                await Task.WhenAny([.. pumps, watch]).ConfigureAwait(false);
            }
            finally
            {
                Unpublish();
                await link.CancelAsync().ConfigureAwait(false);
                closed.TrySetResult();
                await Task.WhenAll(pumps).ConfigureAwait(false);
            }
        }
    }

    private Task[] StartProjections(RemoteHostClient client, CancellationToken cancellationToken)
    {
        var hasRegistry = client.Supports(HostProtocol.SessionRegistryCapability);
        var hasPending = client.Supports(HostProtocol.PendingBroadcastCapability);
        if (!hasRegistry || !hasPending)
            throw new NotSupportedException("桌面 host 版本过旧: 缺少会话登记表或待决广播能力");
        return
        [
            PumpAsync(client.PendingChangesAsync(cancellationToken), change => ReplacePending(change.Pending)),
            PumpAsync(client.HostSessionChangesAsync(cancellationToken), ApplySession),
        ];
    }

    private static async Task PumpAsync<T>(IAsyncEnumerable<T> source, Action<T> apply)
    {
        try
        {
            await foreach (var item in source.ConfigureAwait(false))
                apply(item);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or JsonRpcException)
        {
        }
    }

    /** 心跳失败即结束当前链路; TCP 半开时只有主动探测能发现。 */
    private async Task WatchAsync(RemoteHostClient client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(HeartbeatSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(HeartbeatTimeoutSeconds));
            try
            {
                await client.PingAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or JsonRpcException)
            {
                return;
            }
        }
    }

    private void Publish(HostLease lease)
    {
        lock (_gate)
            _connected.TrySetResult(lease);
    }

    private void Unpublish()
    {
        lock (_gate)
        {
            if (_connected.Task.IsCompleted)
                _connected = new TaskCompletionSource<HostLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void ReplacePending(RemotePendingList pending)
    {
        lock (_gate)
            _pending = pending;
        Changed?.Invoke();
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
        Changed?.Invoke();
    }

    private void SetState(MobileConnectionState state, string? detail)
    {
        State = state;
        Detail = detail;
        Changed?.Invoke();
    }

    private static TimeSpan Backoff(int failures)
        => TimeSpan.FromMilliseconds(Math.Min(MaxBackoffMilliseconds, 500 * Math.Pow(2, Math.Min(failures, 5))));

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
}
