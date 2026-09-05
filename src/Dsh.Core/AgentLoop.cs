using Cordis;
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

    private readonly AgentLoopConfig _config;
    private readonly Func<SessionId, ISessionPersistence>? _persistenceFor;

    public AgentLoop(Context ctx, AgentLoopConfig? config = null, Func<SessionId, ISessionPersistence>? persistenceFor = null)
        : base(ctx, ServiceName)
    {
        _config = config ?? new AgentLoopConfig();
        _persistenceFor = persistenceFor;
        if (_config.MaxParallelToolCalls < 1)
            throw new ArgumentException("maxParallelToolCalls must be a positive integer");
        var agents = ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)
            ?? throw new InvalidOperationException("agent loop requires the agents service");
        agents.SetFactory(this);
        Ctx.Get<SessionProjectionRegistry>(SessionProjectionRegistry.ServiceName, false)
            ?.Register(TurnBoundaryProjectionDefinition.Instance);
    }

    public Task<IAgent> CreateAgent(Context owner, CreateAgentOptions options, CancellationToken signal = default)
    {
        var sessions = Ctx.Get<SessionStore>(SessionStore.ServiceName)
            ?? throw new InvalidOperationException("agent loop requires the sessions service");
        var session = sessions.Create(
            options.SessionId,
            options.Seed,
            new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = options.SessionId ?? SessionId.Create(Guid.NewGuid().ToString()),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Cwd = options.Cwd,
                IsSeeded = options.Seed is not null,
                AgentPreset = options.AgentPreset,
            },
            options.InheritedEventCount);
        var agent = new AgentLoopAgent(owner, session.Id, options.AgentOptions ?? new AgentOptions(), session, LastTurnOf);
        options.Setup?.Invoke(agent.Ctx);
        Ctx.Emit(AgentEventNames.SessionStart, new { Agent = agent, Source = "startup" });
        return Task.FromResult<IAgent>(agent);
    }

    public Task<IAgent> Resume(Context owner, ResumeAgentOptions options, CancellationToken signal = default)
    {
        var persistence = _persistenceFor?.Invoke(options.SessionId)
            ?? throw new InvalidOperationException("no session persistence backend configured for resume");
        using var handle = persistence.Open(options.SessionId, SessionAccess.Write);
        var persisted = handle.Read();
        var closers = SessionRepair.InterruptedTurnClosers(persisted);
        var events = closers.Count > 0 ? [..persisted, ..closers] : persisted;
        var session = Session.FromRestore(options.SessionId, events, handle.Header, handle.InheritedEventCount);
        var sessions = Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        using var detach = sessions.Enter(session, owner);
        sessions.Announce(session);
        var agent = new AgentLoopAgent(owner, session.Id, options.AgentOptions ?? new AgentOptions(), session, LastTurnOf);
        options.Setup?.Invoke(agent.Ctx);
        Ctx.Emit(AgentEventNames.SessionStart, new { Agent = agent, Source = "resume" });
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
