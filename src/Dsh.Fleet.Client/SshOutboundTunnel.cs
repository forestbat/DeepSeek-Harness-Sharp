using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Dsh.Fleet;

/// <summary>Owns only the local SSH processes and streams used to borrow one remote TCP proxy.</summary>
public sealed class SshOutboundTunnel : IAsyncDisposable
{
    private readonly object _gate = new();
    private MachineSpec? _machine;
    private Uri? _proxyUri;
    private readonly bool _localForward;
    private readonly ISshTransportAdapter? _adapter;
    private readonly HashSet<SshProxyStream> _connections = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action<SshProxyStream> _released;
    private SshProxyProcess? _forward;
    private Task<SshProxyProcess>? _forwardStart;
    private int? _forwardPort;
    private Task? _disposal;
    private bool _disposed;
    private bool _lifetimeCancellationRequested;

    public SshOutboundTunnel(MachineSpec machine, ISshTransportAdapter? adapter = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        _proxyUri = MachineStore.ValidateOutboundTunnel(machine)
            ?? throw new InvalidOperationException("The machine has no outbound tunnel configured.");
        _machine = machine;
        _localForward = machine.OutboundTunnel!.Mode == "localForward";
        _adapter = adapter;
        _released = Release;
    }

    public Uri ProxyUri
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _proxyUri!;
            }
        }
    }

    // These are the actual pending/current owner, including during target authentication.
    internal Process? Process { get { lock (_gate) return _forward?.Process; } }
    internal int? LocalPort { get { lock (_gate) return _forwardPort; } }

    public async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        SshProxyStream? connection = null;
        Task<SshProxyProcess>? forwardStart = null;
        MachineSpec? machine = null;
        CancellationTokenSource linked;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (endpoint.Port != _proxyUri!.Port || !endpoint.Host.AsSpan().Trim("[]").Equals(_proxyUri!.IdnHost.AsSpan().Trim("[]"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The connection endpoint does not match the configured remote proxy.");
            cancellationToken.ThrowIfCancellationRequested();
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            if (_localForward)
            {
                // Capture once on entry: an already-entered request must never join a replacement.
                if (_forwardStart is null || _forwardStart.IsCompleted &&
                    (_forwardStart.IsFaulted || _forward?.FailureToken.IsCancellationRequested == true))
                    _forwardStart = StartForwardAsync(_forward, [.. _connections], endpoint);
                forwardStart = _forwardStart;
            }
            else
            {
                machine = _machine;
                connection = new SshProxyStream(new SshProxyProcess(persistent: false, _adapter), ownsProcess: true, _released);
                _connections.Add(connection);
            }
        }
        using (linked)
        {
            try
            {
                if (forwardStart is not null)
                {
                    var owner = await forwardStart.WaitAsync(linked.Token).ConfigureAwait(false);
                    int port;
                    lock (_gate)
                    {
                        ObjectDisposedException.ThrowIf(_disposed, this);
                        linked.Token.ThrowIfCancellationRequested();
                        owner.ThrowIfFailed();
                        // The successful captured task still owns this port; renewal requires failure.
                        port = _forwardPort!.Value;
                        connection = new SshProxyStream(owner, ownsProcess: false, _released);
                        _connections.Add(connection);
                    }
                    await connection.ConnectLoopbackAsync(port, linked.Token).ConfigureAwait(false);
                }
                else
                {
                    var starting = connection!.StartAsync(machine!, endpoint, linked.Token);
                    machine = null;
                    await starting.ConfigureAwait(false);
                }
                return connection!;
            }
            catch
            {
                if (connection is not null)
                    await connection.ReleaseAsync(retryCleanup: false).ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task<SshProxyProcess> StartForwardAsync(SshProxyProcess? previous, SshProxyStream[] oldConnections, DnsEndPoint endpoint)
    {
        // Startup belongs to the tunnel, never to the first request's cancellation token.
        if (previous is not null)
        {
            await Task.WhenAll(oldConnections.Select(connection => connection.ReleaseAsync(retryCleanup: false).AsTask())).ConfigureAwait(false);
            await previous.ReleaseAsync(retryCleanup: false).ConfigureAwait(false);
        }
        SshProxyProcess owner;
        Task starting;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lifetime.Token.ThrowIfCancellationRequested();
            var port = _machine!.OutboundTunnel!.LocalPort;
            if (port is null)
            {
                // One OS candidate, one handoff. SSH reports a lost bind race; never try another port.
                using var candidate = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                candidate.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                port = ((IPEndPoint)candidate.LocalEndPoint!).Port;
            }
            _forwardPort = port;
            _forward = owner = new SshProxyProcess(persistent: true, _adapter);
            starting = owner.StartAsync(_machine, endpoint, port, _lifetime.Token);
        }
        await starting.ConfigureAwait(false);
        return owner;
    }

    private void Release(SshProxyStream connection)
    {
        lock (_gate)
            _connections.Remove(connection);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null && (!_disposal.IsCompleted || _disposal.IsCompletedSuccessfully))
                return new ValueTask(_disposal);
            _disposed = true;
            _machine = null;
            _proxyUri = null;
            _disposal = DisposeConnectionsAsync([.. _connections], _forwardStart);
            return new ValueTask(_disposal);
        }
    }

    private async Task DisposeConnectionsAsync(SshProxyStream[] connections, Task<SshProxyProcess>? forwardStart)
    {
        try
        {
            if (!_lifetimeCancellationRequested)
            {
                _lifetimeCancellationRequested = true;
                await _lifetime.CancelAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await Task.WhenAll(connections.Select(connection => connection.DisposeAsync().AsTask())).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (forwardStart is not null)
                    {
                        try
                        {
                            await forwardStart.ConfigureAwait(false);
                        }
                        catch (Exception error) when (forwardStart.IsFaulted || error is OperationCanceledException)
                        {
                            // The starting request observes startup failure; disposal still owns cleanup.
                        }
                        lock (_gate)
                        {
                            if (ReferenceEquals(_forwardStart, forwardStart))
                                _forwardStart = null;
                        }
                    }
                }
                finally
                {
                    try
                    {
                        if (_forward is not null)
                        {
                            await _forward.DisposeAsync().ConfigureAwait(false);
                            lock (_gate)
                                _forward = null;
                        }
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            if (_connections.Count == 0 && _forward is null)
                                _forwardPort = null;
                        }
                    }
                }
            }
        }
        _lifetime.Dispose();
    }
}
