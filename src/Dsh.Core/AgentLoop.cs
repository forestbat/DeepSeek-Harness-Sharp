using Dsh.Runtime;
using Dsh.Llm;

namespace Dsh.Core;

public sealed class AgentLoopConfig
{
    public const int DefaultMaxParallelToolCalls = 10;

    public int MaxParallelToolCalls { get; init; } = DefaultMaxParallelToolCalls;

    public IReadOnlyList<ConfiguredAgent> Agents { get; init; } = [];
}

public sealed record ConfiguredAgent(
    string Id,
    SessionId? SessionId = null,
    SessionId? ResumeSessionId = null,
    string? Cwd = null,
    AgentOptions? Options = null);

public sealed class AgentLoop : Service, IAgentFactory
{
    public const string ServiceName = "agentLoop";

    private readonly Func<SessionId, ISessionPersistence>? _persistenceFor;

    public AgentLoop(Context ctx, AgentLoopConfig? config = null, Func<SessionId, ISessionPersistence>? persistenceFor = null)
        : base(ctx, ServiceName)
    {
        var resolvedConfig = config ?? new AgentLoopConfig();
        _persistenceFor = persistenceFor;
        if (resolvedConfig.MaxParallelToolCalls < 1)
            throw new ArgumentException("maxParallelToolCalls must be a positive integer");
        var agents = ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false)
            ?? throw new InvalidOperationException("agent loop requires the agents service");
        agents.SetFactory(this);
        Ctx.Get<SessionProjectionRegistry>(SessionProjectionRegistry.ServiceName, false)
            ?.Register(TurnBoundaryProjectionDefinition.Instance);
    }

    public Task<IAgent> CreateAgent(Context owner, CreateAgentOptions options, CancellationToken signal = default)
    {
        var sessions = Ctx.Get<SessionStore>(SessionStore.ServiceName)
            ?? throw new InvalidOperationException("agent loop requires the sessions service");
        var sessionId = options.SessionId ?? SessionId.Create(Guid.NewGuid().ToString());
        var session = sessions.Create(
            sessionId,
            options.Seed,
            new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = sessionId,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Cwd = options.Cwd,
                IsSeeded = options.Seed is not null,
                AgentPreset = options.AgentPreset,
            },
            options.InheritedEventCount);
        var agent = new AgentLoopAgent(owner, session.Id, options.AgentOptions ?? new AgentOptions(), session, LastTurnOf);
        options.Setup?.Invoke(agent.Ctx);
        Ctx.Emit(new AgentSessionStartNotification(agent, "startup"));
        return Task.FromResult<IAgent>(agent);
    }

    public Task<IAgent> Resume(Context owner, ResumeAgentOptions options, CancellationToken signal = default)
    {
        var persistence = _persistenceFor?.Invoke(options.SessionId)
            ?? throw new InvalidOperationException("no session persistence backend configured for resume");
        // 只读取历史; 写入句柄由持久化接线在会话进入 store 后接管, 否则两处会争同一把写锁。
        using var handle = persistence.Open(options.SessionId, SessionAccess.Read);
        var persisted = handle.Read();
        var closers = SessionRepair.InterruptedTurnClosers(persisted);
        var events = closers.Count > 0 ? [.. persisted, .. closers] : persisted;
        var session = Session.FromRestore(options.SessionId, events, handle.Header, handle.InheritedEventCount);
        var sessions = Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        // 与 Create 一致: 会话进入 store 后由 agent 生命周期持有, 不能在这里就解绑, 否则事件不再转发/落盘。
        sessions.Enter(session, owner);
        sessions.Announce(session);
        var agent = new AgentLoopAgent(owner, session.Id, options.AgentOptions ?? new AgentOptions(), session, LastTurnOf);
        options.Setup?.Invoke(agent.Ctx);
        Ctx.Emit(new AgentSessionStartNotification(agent, "resume"));
        return Task.FromResult<IAgent>(agent);
    }

    internal static int LastTurnOf(Session session)
    {
        var lastTurn = 0;
        foreach (var sessionEvent in session.SnapshotEvents())
        {
            if (sessionEvent.Data is TurnStartPayload turnStart)
                lastTurn = turnStart.Turn;
        }
        return lastTurn;
    }
}
