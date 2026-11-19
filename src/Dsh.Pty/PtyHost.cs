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
        PtySession? replaced = null;
        PtySession session;
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var existing))
            {
                // 同 id 复活: 只允许覆盖已经退出的那一份 —— 它已经没有任何活着的子进程,
                // 而正在跑的 pty 被悄悄替换会让原来的 attach 客户端对着一个空壳。
                if (existing.Status != PtySessionStatus.Exited)
                    throw new InvalidOperationException($"PTY session already exists: {sessionId}");
                replaced = existing;
            }

            session = new PtySession(sessionId, info);
            _sessions[sessionId] = session;
        }

        replaced?.Dispose();
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
