using Dsh.Runtime;
using Dsh.Llm;
using Dsh.Runtime.Events;
using Dsh.Core;

namespace Dsh.Agent;

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

public sealed class AgentLoop : Service
{
    public const string ServiceName = "agentLoop";

    private readonly Func<SessionId, ISessionPersistence>? _persistenceFor;

    public int MaxParallelToolCalls { get; }

    public AgentLoop(Context ctx, AgentLoopConfig? config = null, Func<SessionId, ISessionPersistence>? persistenceFor = null)
        : base(ctx, ServiceName)
    {
        var resolvedConfig = config ?? new AgentLoopConfig();
        _persistenceFor = persistenceFor;
        if (resolvedConfig.MaxParallelToolCalls < 1)
            throw new ArgumentException("maxParallelToolCalls must be a positive integer");
        MaxParallelToolCalls = resolvedConfig.MaxParallelToolCalls;
        var agents = ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false)
            ?? throw new InvalidOperationException("agent loop requires the agents service");
        agents.SetFactory(this);
        Ctx.Get<SessionProjectionRegistry>(SessionProjectionRegistry.ServiceName, false)
            ?.Register(TurnBoundaryProjectionDefinition.Instance);
        ctx.On<AgentDisposedNotification>(notification => Release(notification.Agent));
    }

    /** 注册表移除本工厂造出的 agent 时收尾:退订其投影并释放会话(连同子代理会话),持久化随之关闭写句柄。 */
    private void Release(IAgent agent)
    {
        if (agent is not AgentLoopAgent loopAgent)
            return;
        loopAgent.Dispose();
        Ctx.Get<SessionStore>(SessionStore.ServiceName, false)?.Release(loopAgent.Id);
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
        var session = Restore(options.SessionId, persistence, handle);
        var sessions = Ctx.Get<SessionStore>(SessionStore.ServiceName)!;
        // 与 Create 一致: 会话进入 store 后由 agent 生命周期持有, 不能在这里就解绑, 否则事件不再转发/落盘。
        sessions.Enter(session);
        var agent = new AgentLoopAgent(owner, session.Id, options.AgentOptions ?? new AgentOptions(), session, LastTurnOf);
        options.Setup?.Invoke(agent.Ctx);
        Ctx.Emit(new AgentSessionStartNotification(agent, "resume"));
        return Task.FromResult<IAgent>(agent);
    }

    /** 能窗口化就只读尾部一段(超大会话的打开代价由此与历史长度脱钩), 否则整份恢复。 */
    private static Session Restore(SessionId id, ISessionPersistence persistence, ISessionHandle handle)
    {
        if (persistence.TryOpenWindow(id, ResumeTailEvents) is { } paged)
        {
            // 中断收尾只看尾部: 未闭合的 turn / 悬空工具调用只可能出现在最后。
            var closers = SessionRepair.InterruptedTurnClosers(paged.Resident);
            var resident = closers.Count > 0 ? [.. paged.Resident, .. closers] : paged.Resident;
            return Session.FromWindow(id,
                paged with { Resident = resident, TotalEvents = paged.TotalEvents + closers.Count },
                handle.Header, handle.InheritedEventCount);
        }
        var persisted = handle.Read();
        // 只对"本会话自有"的事件补中断收尾; 种子(fork 前缀)是冻结历史, 不在其中(见 InterruptedTurnClosers)。
        var fullClosers = SessionRepair.InterruptedTurnClosers(persisted, handle.InheritedEventCount);
        var events = fullClosers.Count > 0 ? [.. persisted, .. fullClosers] : persisted;
        return Session.FromRestore(id, events, handle.Header, handle.InheritedEventCount);
    }

    /** 窗口化恢复的尾窗大小: 几十个回合的量, 渲染与压缩都在这段里工作。 */
    private const int ResumeTailEvents = 20_000;

    /** 恢复出来的会话从哪个回合接着数: 只看常驻尾窗, 否则窗口化打开等于把整份日志按页读回来。 */
    internal static int LastTurnOf(Session session)
    {
        var lastTurnStart = session.LastResidentEventWhere(sessionEvent => sessionEvent.Data is TurnStartPayload);
        return lastTurnStart?.Data is TurnStartPayload turnStart ? turnStart.Turn : 0;
    }
}
