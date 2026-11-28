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
                AgentId = options.AgentId,
                Cwd = options.Cwd,
                IsSeeded = options.Seed is not null,
                AgentPreset = options.AgentPreset,
            },
            options.InheritedEventCount);
        var agent = new AgentLoopAgent(owner, session.Id, options.AgentOptions ?? new AgentOptions(), session, SessionRepair.LastTurnOf, options.AgentId);
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
        var agent = new AgentLoopAgent(owner, session.Id, options.AgentOptions ?? new AgentOptions(), session, SessionRepair.LastTurnOf, options.AgentId ?? session.Header.AgentId);
        options.Setup?.Invoke(agent.Ctx);
        Ctx.Emit(new AgentSessionStartNotification(agent, "resume"));
        return Task.FromResult<IAgent>(agent);
    }

    /**
     * 恢复策略: **有折叠检查点才窗口化**, 没有就整份恢复。
     *
     * 窗口化本身不省读页: 命中检查点时它只折尾窗(比整份快 5–6 倍), 没有检查点时它要把前缀一页页折回来,
     * 反而比整份恢复慢 1.2–3.4 倍。所以判据是检查点在不在, 而不是日志有多大。
     * 整份恢复之后第一次页边界就会写出第一份检查点, 收益落在下一次打开。
     */
    private static Session Restore(SessionId id, ISessionPersistence persistence, ISessionHandle handle)
    {
        var adopted = persistence.TryLoadSurfaceFold(id);
        if (adopted is not null && persistence.TryOpenWindow(id, ResumeTailEvents) is { } paged)
        {
            // 中断收尾只看尾部: 未闭合的 turn / 悬空工具调用只可能出现在最后。
            // 第二个参数是"传入列表内的偏移": 窗口会话里自有的起点是 ResidentFrom, 所以 fork 种子前缀
            // 在窗口内那一段要按 (inherited - ResidentFrom) 跳过, 否则会给冻结前缀补合成事件。
            var closers = SessionRepair.InterruptedTurnClosers(
                paged.Resident,
                Math.Max(0, handle.InheritedEventCount - paged.ResidentFrom));
            var resident = closers.Count > 0 ? [.. paged.Resident, .. closers] : paged.Resident;
            // 检查点里的表面折叠结果直接用: 这条路径因此不必再折前缀(那是 5–21s 的量级)。
            var windowed = Session.FromWindow(id,
                paged with { Resident = resident, TotalEvents = paged.TotalEvents + closers.Count },
                handle.Header, handle.InheritedEventCount, adopted);
            // 旧检查点里没有请求头/请求上下文(那时还不进快照): 从日志尾部往前找最近的那两条补上,
            // 而不是"缺这两项就整份恢复"—— 那会把 2 秒的冷启动变成 5 秒, 换来一个后台就能补的字段。
            if (adopted.RequestHeader is null || adopted.RequestContext is null)
            {
                // 后台补: 这段扫描可能读到巨型帧(单页 3~5s), 放在同步路径上等于把窗口化的收益全吃掉。
                // 请求头是"下一轮发请求"才用得到的东西, 晚几百毫秒没有代价。
                _ = Task.Run(() => BackfillFoldMetaAsync(persistence, id, windowed), CancellationToken.None);
            }
            return windowed;
        }
        var persisted = handle.Read();
        // 只对"本会话自有"的事件补中断收尾; 种子(fork 前缀)是冻结历史, 不在其中(见 InterruptedTurnClosers)。
        var fullClosers = SessionRepair.InterruptedTurnClosers(persisted, handle.InheritedEventCount);
        var events = fullClosers.Count > 0 ? [.. persisted, .. fullClosers] : persisted;
        return Session.FromRestore(id, events, handle.Header, handle.InheritedEventCount);
    }

    /** 从日志里补回请求头/上下文(后台): 从尾部往前按页找, 找到就停; 有上限, 找不到就算了(按 Options 走)。 */
    private static async Task BackfillFoldMetaAsync(ISessionPersistence persistence, SessionId id, Session session)
    {
        const int page = 4096;
        const int maxPages = 16;
        await Task.Yield();
        ISessionHandle handle;
        try
        {
            handle = persistence.Open(id, SessionAccess.Read);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return;
        }
        using var _ = handle;
        EpochHeader? header = null;
        RequestContextPayload? context = null;
        var from = handle.Count;
        for (var read = 0; read < maxPages && from > 0; read += 1)
        {
            var start = Math.Max(0, from - page);
            IReadOnlyList<SessionEvent> slice;
            try
            {
                slice = handle.Read(start, (int)(from - start));
            }
            catch (Exception error) when (error is IOException or FormatException or InvalidOperationException)
            {
                return;
            }
            for (var index = slice.Count - 1; index >= 0; index -= 1)
            {
                switch (slice[index].Data)
                {
                    case RequestHeaderPayload request when header is null:
                        header = request.Header;
                        break;
                    case RequestContextPayload request when context is null:
                        context = request;
                        break;
                }
                if (header is not null && context is not null)
                {
                    session.AdoptFoldMeta(header, context);
                    return;
                }
            }
            from = start;
        }
        session.AdoptFoldMeta(header, context);
    }


    /** 窗口化恢复的尾窗大小: 几十个回合的量, 渲染与压缩都在这段里工作。 */
    private const int ResumeTailEvents = 20_000;
}
