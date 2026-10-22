using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Dsh.RemoteHost;
using Dsh.Transport;
using Renci.SshNet;

namespace Dsh.Android.Services;

public enum MobileConnectionState
{
    Offline,
    Connecting,
    Connected,
    Reconnecting,
}

/**
 * 手机到桌面 host 的一条链路: 只负责连接、心跳与重连, 会话语义全部由 RemoteHostClient 提供。
 * 传输可走局域网 TCP + 握手令牌, 或 SSH exec 通道里跑 `dsharp host serve --stdio`;
 * 订阅复用 RemoteHostSubscriptions, 因此断线后的续订从最后一条事件之后继续。
 */
public sealed class MobileHostConnection : IAsyncDisposable
{
    private const int TcpConnectTimeoutSeconds = 8;
    private const int SshConnectTimeoutSeconds = 20;
    private const int HeartbeatSeconds = 15;
    private const int HeartbeatTimeoutSeconds = 10;
    private const int MaxBackoffMilliseconds = 10_000;

    private readonly HostProfile _profile;
    private readonly string? _privateKeyPem;
    private readonly Action<string> _trustFingerprint;
    private string _fingerprint;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RemoteHostSession> _sessions = new(StringComparer.Ordinal);
    private readonly RemoteHostSubscriptions _subscriptions;
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource<HostLease> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RemotePendingList _pending = new([], []);
    private Task? _loop;

    public MobileHostConnection(HostProfile profile, string? privateKeyPem, Action<string> trustFingerprint)
    {
        _profile = profile;
        _privateKeyPem = privateKeyPem;
        _trustFingerprint = trustFingerprint;
        _fingerprint = profile.SshFingerprint;
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
        await using var stream = await OpenStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var client = await RemoteHostClient.ConnectAsync(stream, _profile.Token, "dsh-android", cancellationToken).ConfigureAwait(false);
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

    private Task<Stream> OpenStreamAsync(CancellationToken cancellationToken)
        => _profile.Transport == HostTransportKind.Ssh
            ? OpenSshStreamAsync(cancellationToken)
            : OpenTcpStreamAsync(cancellationToken);

    private async Task<Stream> OpenTcpStreamAsync(CancellationToken cancellationToken)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromSeconds(TcpConnectTimeoutSeconds));
            await tcp.ConnectAsync(_profile.Host, _profile.Port, attempt.Token).ConfigureAwait(false);
            return new NetworkStream(tcp.Client, ownsSocket: true);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /**
     * SSH 通道: 读远端 daemon 的端口与本地端点令牌, 用 SSH 端口转发连到它的 loopback 监听。
     * 不走 sshd 的 stdio 桥(实测在 Windows sshd 下 stdin 拿不到数据), 且不需要在远端额外起进程。
     */
    private async Task<Stream> OpenSshStreamAsync(CancellationToken cancellationToken)
    {
        if (_privateKeyPem is not { Length: > 0 } pem)
            throw new InvalidOperationException("缺少 SSH 私钥，请先在本机生成公钥并加入桌面机");
        var keyFile = new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(pem)));
        var auth = new PrivateKeyAuthenticationMethod(_profile.SshUser, keyFile);
        var connectionInfo = new ConnectionInfo(_profile.Host, _profile.Port, _profile.SshUser, auth)
        {
            Timeout = TimeSpan.FromSeconds(SshConnectTimeoutSeconds),
        };
        var client = new SshClient(connectionInfo);
        var mismatch = "";
        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = Convert.ToBase64String(SHA256.HashData(e.HostKey));
            if (_fingerprint.Length == 0)
            {
                _fingerprint = fingerprint;
                _trustFingerprint(fingerprint);
                return;
            }
            if (!string.Equals(_fingerprint, fingerprint, StringComparison.Ordinal))
            {
                mismatch = $"SSH 主机密钥不匹配(期望 {_fingerprint}, 实际 {fingerprint})";
                e.CanTrust = false;
            }
        };
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromSeconds(SshConnectTimeoutSeconds));
            await client.ConnectAsync(attempt.Token).ConfigureAwait(false);
            if (mismatch.Length > 0)
                throw new IOException(mismatch);
            var portText = await ReadRemoteAsync(client, "%USERPROFILE%\\.dsh\\run\\host.port", cancellationToken).ConfigureAwait(false);
            if (!int.TryParse(portText.Trim(), out var daemonPort))
                throw new IOException("远端 dsharp daemon 未运行（缺少 host.port），请先在桌面机运行 dsharp host serve");
            var localToken = (await ReadRemoteAsync(client, "%USERPROFILE%\\.dsh\\run\\host.token", cancellationToken).ConfigureAwait(false)).Trim();
            if (localToken.Length == 0)
                throw new IOException("远端 dsharp daemon 没有提供本地访问令牌");
            var forward = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", (uint)daemonPort);
            client.AddForwardedPort(forward);
            forward.Start();
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connect.CancelAfter(TimeSpan.FromSeconds(TcpConnectTimeoutSeconds));
                await tcp.ConnectAsync(IPAddress.Loopback, (int)forward.BoundPort, connect.Token).ConfigureAwait(false);
                var stream = tcp.GetStream();
                await stream.WriteAsync(Encoding.UTF8.GetBytes(localToken), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                return new SshTunnelStream(client, forward, tcp, stream);
            }
            catch
            {
                tcp.Dispose();
                forward.Stop();
                throw;
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<string> ReadRemoteAsync(SshClient client, string file, CancellationToken cancellationToken)
    {
        using var command = client.CreateCommand($"cmd /c type \"{file}\"");
        await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return command.Result;
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

    /** 一条经 SSH 端口转发到远端 daemon 的 TCP 流; 释放时一并停掉转发并断开 SSH。 */
    private sealed class SshTunnelStream(SshClient client, ForwardedPortLocal forward, TcpClient tcp, Stream stream) : Stream
    {
        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => stream.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => stream.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => stream.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => stream.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => stream.WriteAsync(buffer, cancellationToken);

        public override void Flush() => stream.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => stream.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                CloseAll();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            CloseAll();
            return base.DisposeAsync();
        }

        private void CloseAll()
        {
            try
            {
                stream.Dispose();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
            }
            try
            {
                tcp.Dispose();
            }
            catch (Exception error) when (error is ObjectDisposedException)
            {
            }
            try
            {
                forward.Stop();
                forward.Dispose();
            }
            catch (Exception error) when (error is ObjectDisposedException or Renci.SshNet.Common.SshException)
            {
            }
            client.Dispose();
        }
    }
}