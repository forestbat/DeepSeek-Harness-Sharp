using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Dsh.Runtime;

/// <summary>Signals an unexpected transport loss, excluding normal disposal and caller cancellation.</summary>
public interface IOutboundTransportLifetime
{
    CancellationToken FailureToken { get; }
    /// <summary>Stable identity of the failure source, whose lifetime may exceed one connection.</summary>
    object FailureOwner { get; }
}

/// <summary>Keeps pools only for live route generations, never replaying a retired send.</summary>
internal sealed class OutboundHttpHandler(
    OutboundProxy proxy,
    bool allowAutoRedirect,
    DecompressionMethods automaticDecompression) : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly Dictionary<(long Generation, Uri? Endpoint), Pool> _pools = [];
    private readonly CookieContainer _cookies = new();
    private bool _disposed;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var snapshot = proxy.CaptureRoute(request.RequestUri!);
        ValidateProtocol(snapshot, request);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, snapshot.CancellationToken);
        linked.Token.ThrowIfCancellationRequested();
        var pool = GetPool(snapshot);
        var response = await pool.SendAsync(request, linked.Token).ConfigureAwait(false);
        // QUIC body reads bypass ConnectCallback, so their responses also belong to the route generation.
        snapshot.Register(response);
        return response;
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var snapshot = proxy.CaptureRoute(request.RequestUri!);
        ValidateProtocol(snapshot, request);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, snapshot.CancellationToken);
        linked.Token.ThrowIfCancellationRequested();
        var response = GetPool(snapshot).Send(request, linked.Token);
        snapshot.Register(response);
        return response;
    }

    private static void ValidateProtocol(OutboundProxy.RouteSnapshot snapshot, HttpRequestMessage request)
    {
        if (snapshot.HasBorrowedTransport && request.Version.Major >= 3 && request.VersionPolicy != HttpVersionPolicy.RequestVersionOrLower)
            throw new NotSupportedException("Borrowed SSH proxy routes support HTTP/1.1 and HTTP/2, not forced HTTP/3.");
    }

    private Pool GetPool(OutboundProxy.RouteSnapshot snapshot)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            snapshot.ThrowIfRetired();
            var key = (snapshot.Generation, snapshot.ProxyUri);
            if (_pools.TryGetValue(key, out var existing))
                return existing;
            var pool = new Pool(this, snapshot, allowAutoRedirect, automaticDecompression);
            try
            {
                snapshot.Register(pool);
                snapshot.ThrowIfRetired();
                _pools.Add(key, pool);
                return pool;
            }
            catch
            {
                pool.Dispose();
                throw;
            }
        }
    }

    private void Remove(Pool pool, OutboundProxy.RouteSnapshot snapshot)
    {
        lock (_gate)
        {
            var key = (snapshot.Generation, snapshot.ProxyUri);
            if (_pools.TryGetValue(key, out var existing) && ReferenceEquals(existing, pool))
                _pools.Remove(key);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Pool[] pools;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                pools = [.. _pools.Values];
                _pools.Clear();
            }
            OutboundResourceLifetime.DisposeResources(pools);
        }
        base.Dispose(disposing);
    }

    private sealed class Pool : IDisposable
    {
        private readonly OutboundHttpHandler _owner;
        private readonly OutboundProxy.RouteSnapshot _snapshot;
        private readonly OutboundResourceLifetime _connections = new();
        private HttpMessageInvoker? _invoker;
        private int _disposed;

        internal Pool(OutboundHttpHandler owner, OutboundProxy.RouteSnapshot snapshot, bool redirects, DecompressionMethods decompression)
        {
            _owner = owner;
            _snapshot = snapshot;
            _invoker = new HttpMessageInvoker(snapshot.CreatePooledHandler(redirects, decompression, _connections, owner._cookies));
        }

        internal Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var invoker = Volatile.Read(ref _invoker);
            if (invoker is null)
                throw new OperationCanceledException("The outbound connection pool has been retired.", token);
            return invoker.SendAsync(request, token);
        }

        internal HttpResponseMessage Send(HttpRequestMessage request, CancellationToken token)
        {
            var invoker = Volatile.Read(ref _invoker);
            if (invoker is null)
                throw new OperationCanceledException("The outbound connection pool has been retired.", token);
            return invoker.Send(request, token);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _snapshot.Unregister(this);
            _owner.Remove(this, _snapshot);
            try
            {
                var retired = _connections.BeginRetirement();
                _connections.CompleteRetirement(retired);
            }
            finally
            {
                Interlocked.Exchange(ref _invoker, null)?.Dispose();
            }
        }
    }
}

/// <summary>
/// Weak registration preserves the normal raw-handler disposal API without rooting disposed handlers.
/// Streams explicitly unregister on close.
/// </summary>
internal sealed class OutboundResourceLifetime
{
    private static readonly object Registered = new();
    private readonly object _gate = new();
    private readonly ConditionalWeakTable<IDisposable, object> _resources = new();
    private ConditionalWeakTable<object, TransportFailureRegistration>? _transportFailures;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _retired;

    internal CancellationToken Token => _cancellation.Token;

    internal void ThrowIfRetired()
    {
        if (Volatile.Read(ref _retired))
            throw new OperationCanceledException("The outbound route has been retired.", Token);
    }

    internal void Register(IDisposable resource)
    {
        lock (_gate)
        {
            if (!_retired)
            {
                _resources.Add(resource, Registered);
                return;
            }
        }
        resource.Dispose();
        throw new OperationCanceledException("The outbound route has been retired.", Token);
    }

    internal void Unregister(IDisposable resource)
    {
        lock (_gate)
            _resources.Remove(resource);
    }

    internal void ObserveTransportFailure(IOutboundTransportLifetime transport, OutboundProxy.RouteGeneration route)
    {
        var owners = Volatile.Read(ref _transportFailures);
        if (owners is null)
        {
            lock (_gate)
                owners = _transportFailures ??= new();
        }
        var owner = transport.FailureOwner;
        lock (owners)
        {
            if (owners.TryGetValue(owner, out _))
                return;
            var registration = new TransportFailureRegistration(transport.FailureToken, route);
            Register(registration);
            owners.Add(owner, registration);
        }
    }

    private sealed class TransportFailureRegistration(CancellationToken failure, OutboundProxy.RouteGeneration route) : IDisposable
    {
        private CancellationTokenRegistration _registration = failure.UnsafeRegister(
            static state => ((OutboundProxy.RouteGeneration)state!).ReportTransportFailure(), route);
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _registration.Unregister();
            _registration = default;
        }
    }

    internal IDisposable[] BeginRetirement()
    {
        lock (_gate)
        {
            if (_retired)
                return [];
            Volatile.Write(ref _retired, true);
            var resources = _resources.Select(pair => pair.Key).ToArray();
            _resources.Clear();
            return resources;
        }
    }

    internal void CompleteRetirement(IDisposable[] resources)
    {
        try
        {
            _cancellation.Cancel();
        }
        finally
        {
            DisposeResources(resources);
        }
    }

    internal static void DisposeResources(IDisposable[] resources)
    {
        List<Exception>? failures = null;
        foreach (var resource in resources)
        {
            try
            {
                resource.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        if (failures is not null)
            throw new AggregateException("Outbound connection retirement failed.", failures);
    }
}

/// <summary>Tracks the real transport, including idle connections and post-header body reads.</summary>
internal sealed class OutboundConnectionStream : Stream
{
    private readonly Stream _inner;
    private readonly OutboundResourceLifetime _generation;
    private readonly OutboundResourceLifetime? _connections;
    private int _disposed;

    private OutboundConnectionStream(Stream inner, OutboundResourceLifetime generation, OutboundResourceLifetime? connections)
    {
        _inner = inner;
        _generation = generation;
        _connections = connections;
    }

    internal static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken token,
        OutboundProxy.RouteGeneration route,
        OutboundResourceLifetime? connections,
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? connector,
        Uri? proxyEndpoint)
    {
        var generation = route.Lifetime;
        generation.ThrowIfRetired();
        connections?.ThrowIfRetired();
        using var linked = connections is null
            ? CancellationTokenSource.CreateLinkedTokenSource(token, generation.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(token, generation.Token, connections.Token);
        var stream = await ConnectTransportAsync(context, linked.Token, route, proxyEndpoint, connector).ConfigureAwait(false);
        var tracked = new OutboundConnectionStream(stream, generation, connections);
        try
        {
            generation.Register(tracked);
            connections?.Register(tracked);
            if (stream is IOutboundTransportLifetime transport)
                generation.ObserveTransportFailure(transport, route);
            generation.ThrowIfRetired();
            connections?.ThrowIfRetired();
            linked.Token.ThrowIfCancellationRequested();
            return tracked;
        }
        catch
        {
            tracked.Dispose();
            throw;
        }
    }

    private static ValueTask<Stream> ConnectTransportAsync(
        SocketsHttpConnectionContext context, CancellationToken token,
        OutboundProxy.RouteGeneration route, Uri? proxyEndpoint,
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? connector)
    {
        if (route.Connect is { } borrowed && proxyEndpoint is not null)
        {
            if (context.DnsEndPoint.Port != proxyEndpoint.Port
                || !context.DnsEndPoint.Host.AsSpan().Trim("[]").Equals(proxyEndpoint.IdnHost.AsSpan().Trim("[]"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The borrowed connector can only open the captured proxy endpoint.");
            return borrowed(context.DnsEndPoint, token);
        }
        return connector is null ? ConnectSocketAsync(context, token) : connector(context, token);
    }

    private static async ValueTask<Stream> ConnectSocketAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override bool CanTimeout => _inner.CanTimeout;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override int ReadTimeout { get => _inner.ReadTimeout; set => _inner.ReadTimeout = value; }
    public override int WriteTimeout { get => _inner.WriteTimeout; set => _inner.WriteTimeout = value; }
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    public override int ReadByte() => _inner.ReadByte();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
    public override void WriteByte(byte value) => _inner.WriteByte(value);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.WriteAsync(buffer, offset, count, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _inner.Dispose();
            }
            finally
            {
                _generation.Unregister(this);
                _connections?.Unregister(this);
            }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _generation.Unregister(this);
            _connections?.Unregister(this);
            GC.SuppressFinalize(this);
        }
    }
}
