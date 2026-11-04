using Dsh.Core;

namespace Dsh.Ide.Bridge;

/// <summary>Routes to an explicitly selected connection or the authenticated origin of a new session.</summary>
public sealed class EditorClientDispatcher : IDisposable
{
    public const string ServiceName = "editorClientDispatcher";
    private readonly object _gate = new();
    private readonly Dictionary<string, Client> _clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessions = new(StringComparer.Ordinal);
    private readonly AsyncLocal<string?> _origin = new();
    private bool _disposed;

    public IReadOnlyList<EditorClientInfo> Clients
    {
        get { lock (_gate) return _clients.Keys.Order(StringComparer.Ordinal).Select(id => new EditorClientInfo(id)).ToArray(); }
    }

    /** Resolves Automatic to the client only when the owning remote client has a trusted mapping. */
    public EditorExecutionTarget? TargetFor(string? sessionId)
    {
        var client = ClientFor(sessionId);
        return client is null ? null : client.IsLocalMachine || !client.HasMappings ? EditorExecutionTarget.Local : EditorExecutionTarget.Client;
    }

    /** Null when no IDE client owns the request. */
    public bool? TryIsLocalMachine(string? sessionId) => ClientFor(sessionId)?.IsLocalMachine;

    private Client? ClientFor(string? sessionId)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (sessionId is { } session && _sessions.TryGetValue(session, out var id) && _clients.TryGetValue(id, out var bound))
                return bound;
            return _origin.Value is { } origin && _clients.TryGetValue(origin, out var originClient) ? originClient : null;
        }
    }

    public IDisposable RegisterClient(string clientId, Func<EditorRequest, CancellationToken, ValueTask<EditorResult>> handler, CancellationToken lifetime = default, bool isLocalMachine = false, bool hasMappings = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(handler);
        Client client;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clients.ContainsKey(clientId))
                throw new InvalidOperationException("An IDE handler is already registered for this connection.");
            client = new Client(this, clientId, handler, isLocalMachine, hasMappings);
            _clients.Add(clientId, client);
        }
        client.ObserveLifetime(lifetime);
        return client;
    }

    public IDisposable? EnterClientScope(string clientId)
    {
        lock (_gate)
        {
            if (_disposed || !_clients.ContainsKey(clientId)) return null;
        }
        var previous = _origin.Value;
        _origin.Value = clientId;
        return new ClientScope(_origin, previous);
    }

    public void BindCurrentClient(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            if (!_disposed && !_sessions.ContainsKey(sessionId) && _origin.Value is { } origin && _clients.ContainsKey(origin))
                _sessions[sessionId] = origin;
        }
    }

    public void SelectClient(string sessionId, string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_clients.ContainsKey(clientId))
                throw new InvalidOperationException("The selected IDE client is not connected.");
            _sessions[sessionId] = clientId;
        }
    }

    public async ValueTask<EditorResult> LaunchAsync(EditorRequest request, CancellationToken cancellationToken = default)
    {
        Client? client = null;
        CancellationTokenSource? dispatch = null;
        CancellationToken clientLifetime = default;
        lock (_gate)
        {
            if (!_disposed && request.SessionId is { } session && _sessions.TryGetValue(session, out var id))
                _clients.TryGetValue(id, out client);
            if (!_disposed && client is null && _origin.Value is { } origin && _clients.TryGetValue(origin, out client)
                && request.SessionId is { } newSession)
                _sessions[newSession] = origin;
            if (client is not null)
            {
                clientLifetime = client.Lifetime.Token;
                dispatch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, clientLifetime);
            }
        }
        if (client is null)
            return new EditorResult { Error = EditorErrorCode.CLIENT_UNAVAILABLE, Message = "No IDE client is selected for this session. Select a connected client or use --print." };
        using var linked = dispatch!;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            return await client.Handler(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && clientLifetime.IsCancellationRequested)
        {
            return new EditorResult { Error = EditorErrorCode.CLIENT_UNAVAILABLE, Message = "The selected IDE client disconnected." };
        }
    }

    private void Remove(Client client)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue(client.Id, out var current) && ReferenceEquals(current, client))
            {
                _clients.Remove(client.Id);
                foreach (var session in _sessions.Where(pair => pair.Value == client.Id).Select(pair => pair.Key).ToArray())
                    _sessions.Remove(session);
            }
        }
    }

    public void Dispose()
    {
        Client[] clients;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            clients = _clients.Values.ToArray();
            _clients.Clear();
            _sessions.Clear();
        }
        foreach (var client in clients) client.Dispose();
    }

    private sealed class ClientScope(AsyncLocal<string?> origin, string? previous) : IDisposable
    {
        public void Dispose() => origin.Value = previous;
    }

    private sealed class Client(EditorClientDispatcher owner, string id, Func<EditorRequest, CancellationToken, ValueTask<EditorResult>> handler, bool isLocalMachine, bool hasMappings) : IDisposable
    {
        private CancellationTokenRegistration _registration;
        private int _disposed;
        public string Id { get; } = id;
        public bool IsLocalMachine { get; } = isLocalMachine;
        public bool HasMappings { get; } = hasMappings;
        public Func<EditorRequest, CancellationToken, ValueTask<EditorResult>> Handler { get; } = handler;
        public CancellationTokenSource Lifetime { get; } = new();
        public void ObserveLifetime(CancellationToken token)
        {
            var registration = token.UnsafeRegister(static state => ((Client)state!).Dispose(), this);
            _registration = registration;
            if (Volatile.Read(ref _disposed) != 0) registration.Unregister();
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.Remove(this);
            _registration.Unregister();
            try { Lifetime.Cancel(); }
            finally { Lifetime.Dispose(); }
        }
    }
}
