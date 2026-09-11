namespace Dsh.Pty;

public sealed class PtyHost : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<PtySessionId, PtySession> _sessions = [];
    private static readonly Lazy<PtyHost> DefaultInstance = new(() => new PtyHost());

    public static PtyHost Default => DefaultInstance.Value;

    public async Task<PtySession> StartAsync(PtyStartInfo info, string? id = null, CancellationToken cancellationToken = default)
    {
        var sessionId = PtySessionId.Create(id ?? $"pty-{Guid.NewGuid():N}");
        lock (_gate)
        {
            if (_sessions.ContainsKey(sessionId))
                throw new InvalidOperationException($"PTY session already exists: {sessionId}");
        }

        var session = new PtySession(sessionId, info);
        lock (_gate)
            _sessions[sessionId] = session;

        try
        {
            await session.StartAsync(cancellationToken);
            return session;
        }
        catch
        {
            lock (_gate)
                _sessions.Remove(sessionId);
            session.Dispose();
            throw;
        }
    }

    public IReadOnlyList<PtySessionInfo> List()
    {
        lock (_gate)
            return _sessions.Values.Select(session => session.ToInfo()).ToList();
    }

    public PtySession? Get(string id)
    {
        var sessionId = PtySessionId.Create(id);
        lock (_gate)
            return _sessions.GetValueOrDefault(sessionId);
    }

    public void Attach(string id)
        => Get(id)?.Attach();

    public void Detach(string id)
        => Get(id)?.Detach();

    public async Task<bool> StopAsync(string id)
    {
        PtySession? session;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(PtySessionId.Create(id), out session))
                return false;
        }

        await session.StopAsync();
        lock (_gate)
            _sessions.Remove(PtySessionId.Create(id));
        return true;
    }

    public void Dispose()
    {
        List<PtySession> sessions;
        lock (_gate)
        {
            sessions = _sessions.Values.ToList();
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            session.StopAsync().GetAwaiter().GetResult();
            session.Dispose();
        }
    }
}
