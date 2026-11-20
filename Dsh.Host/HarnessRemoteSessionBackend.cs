using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Runtime.Events;
using Dsh.Agent;

namespace Dsh.Host;

/** Core harness sessions and their ordered history/live event stream. */
internal sealed class HarnessRemoteSessionBackend(HarnessRemoteRuntime runtime, ExternalAgentSessions agents) : IRemoteSessionBackend
{
    public Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        var byId = new Dictionary<string, RemoteSessionSummary>(StringComparer.Ordinal);
        foreach (var snapshot in runtime.Persistence?.List() ?? [])
        {
            // 空会话(还没有事件)不落盘, 但内存里可能存在: 只有写过事件的会话才出现在列表里。
            // EventCount 为 null 表示磁盘上的日志(实现不为此数事件), 不能当作空会话丢掉。
            if (snapshot.Header.IsSubagent || snapshot.EventCount == 0)
                continue;
            byId[snapshot.Header.Id.Value] = Describe(snapshot.Header);
        }
        foreach (var agent in runtime.Registry.List())
        {
            // 空会话判定只看序号: SnapshotEvents() 在窗口会话里会把整份日志按页读回来, 而这里只想知道有没有事件。
            if (agent.Session.Header.IsSubagent || agent.Session.Seq == 0)
                continue;
            byId[agent.Id.Value] = Describe(agent);
        }
        return Task.FromResult<IReadOnlyList<RemoteSessionSummary>>([.. byId.Values.OrderByDescending(info => info.UpdatedAt)]);
    }

    public async Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken cancellationToken)
    {
        var resolved = HostPathPolicy.Expand(cwd);
        var handle = await runtime.Registry.Create(new CreateAgentOptions(Cwd: resolved, AgentOptions: runtime.DefaultAgentOptions), cancellationToken).ConfigureAwait(false);
        runtime.Own(handle);
        runtime.Track(handle.Agent);
        return Describe(handle.Agent);
    }

    public async Task<RemoteSessionSummary> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var id = SessionId.Create(sessionId);
        if (runtime.Registry.Get(id) is { } live)
        {
            runtime.Track(live);
            return Describe(live);
        }
        var handle = await runtime.Registry.Resume(new ResumeAgentOptions(id, runtime.DefaultAgentOptions), cancellationToken).ConfigureAwait(false);
        runtime.Own(handle);
        runtime.Track(handle.Agent);
        return Describe(handle.Agent);
    }

    /** 返回历史的一段; 分页由 RemoteHostServer 按帧预算切, 所以这里的 TotalEventCount 是整段条数。 */
    public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken)
    {
        var id = SessionId.Create(sessionId);
        var approvals = runtime.App.Ctx.Get<ApprovalService>(ApprovalService.ServiceName, false);
        if (runtime.Store.Get(id) is { } session)
        {
            return Task.FromResult(new RemoteSessionSnapshot(session.Header, session.SnapshotEvents(), session.Seq,
                approvals?.EffectivePolicy(session).ToString()));
        }
        using var handle = runtime.Persistence?.Open(id, SessionAccess.Read)
            ?? throw new InvalidOperationException($"unknown session: {sessionId}");
        // 会话不在内存里时也要窗口化: 只为一个策略值和一页历史把 383MB 日志整段解析成事件不划算。
        if (runtime.Persistence?.TryOpenWindow(id, SnapshotTailEvents) is { } window)
        {
            return Task.FromResult(new RemoteSessionSnapshot(handle.Header, window.Resident, window.TotalEvents,
                approvals?.EffectivePolicy(window.Resident).ToString()));
        }
        // 窗口化不可用(无帧视图/索引损坏/尾窗读不出)才整段读。
        var events = handle.Read();
        // 只为一个策略值重建整篇派生状态(surface 折叠等)不划算: 策略就藏在事件流里。
        return Task.FromResult(new RemoteSessionSnapshot(handle.Header, events, events.Count,
            approvals?.EffectivePolicy(events).ToString()));
    }

    /** 窗口化打开的尾窗: 服务一页历史足够, 剩下的按页取。 */
    private const int SnapshotTailEvents = 20_000;

    public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken)
    {
        if (agents.IsAcp(sessionId))
        {
            agents.Prompt(sessionId, text);
            return Task.CompletedTask;
        }
        var content = new List<ContentBlock>();
        if (text.Length > 0)
            content.Add(new TextBlock(text));
        if (images.Count > 0 && runtime.App.Ctx.Get<IAttachmentStore>(FileAttachmentStore.ServiceName, false) is { } store)
        {
            foreach (var image in images)
                content.Add(new ImageBlock(store.Put(Convert.FromBase64String(image.Base64), image.MediaType, image.Width, image.Height, image.Name)));
        }
        runtime.Require(sessionId).Followup(MessageFactory.CreateUserMessage(content));
        return Task.CompletedTask;
    }

    /** Interrupt abandons this turn and cancels its outstanding approval/question decisions. */
    public Task InterruptAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (agents.IsAcp(sessionId))
            return agents.CancelAsync(sessionId, cancellationToken);
        runtime.Require(sessionId).Cancel(new AgentCancelCause.User());
        runtime.App.Ctx.LoggerFor("host").Info($"session stop: session={sessionId} cause=user");
        runtime.Pending.CancelSession(sessionId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken)
    {
        var tools = runtime.App.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName, false);
        if (tools is null)
            return Task.FromResult<IReadOnlyList<string>>([]);
        var scope = runtime.Registry.Get(SessionId.Create(sessionId))?.ScopeKey;
        IReadOnlyList<string> names = [.. tools.Schemas(scope).Select(schema => schema.Name)];
        return Task.FromResult(names);
    }

    /** Install live listeners before taking history so subscribe replies cannot lose subsequent events. */
    public Task<RemoteHostStream<RemoteSessionEvent>> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
    {
        var id = SessionId.Create(sessionId);
        var session = runtime.Store.Get(id);
        var stored = session is null ? runtime.Persistence?.Stat(id) : null;
        if (session is null && stored is null)
            throw new InvalidOperationException($"unknown session: {sessionId}");
        var reset = fromSeq > (session?.Seq ?? stored?.EventCount ?? long.MaxValue);
        var buffer = Channel.CreateUnbounded<RemoteSessionEvent>();
        var unsubscribe = runtime.App.Ctx.On<SessionEventNotification>(notification =>
        {
            if (notification.Session.Id == id)
                buffer.Writer.TryWrite(new RemoteSessionEvent(sessionId, notification.Event));
        });
        var unsubscribeTruncate = runtime.App.Ctx.On<SessionTruncateNotification>(notification =>
        {
            if (notification.Session.Id != id)
                return;
            // 撤回(就地截断)只告诉读者"保留到第几条": 客户端按序号删掉自己那边的尾部即可,
            // 把整段历史再灌一遍会让界面整屏重建, 用户看到视口先跳到会话头再被一趟趟拉回末尾。
            buffer.Writer.TryWrite(new RemoteSessionEvent(sessionId,
                TruncatedTo: notification.EventCount, TruncatedAt: notification.At));
        });
        ValueTask Release()
        {
            unsubscribeTruncate();
            unsubscribe();
            buffer.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
        try
        {
            var start = reset ? 0 : fromSeq;
            var total = session?.Seq ?? stored?.EventCount;
            // 客户端通常已经拿到整段快照再来订阅: 起点追平条数时没必要为一片空白解析整份日志。
            // 起点落后时只读 [start, total) —— 带长度的读走命中帧, 不必展开整份日志。
            var history = session is not null
                ? session.SnapshotEvents(start)
                : total is { } known && start >= known
                    ? []
                    : ReadPersisted(id, start, total);
            return Task.FromResult(new RemoteHostStream<RemoteSessionEvent>(
                LiveEventsAsync(sessionId, history, buffer, fromSeq, reset, cancellationToken), Release));
        }
        catch
        {
            Release().GetAwaiter().GetResult();
            throw;
        }
    }

    private static async IAsyncEnumerable<RemoteSessionEvent> LiveEventsAsync(
        string sessionId,
        IReadOnlyList<SessionEvent> history,
        Channel<RemoteSessionEvent> buffer,
        long fromSeq,
        bool reset,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var next = reset ? 0 : fromSeq;
        if (reset)
            yield return new RemoteSessionEvent(sessionId, Reset: true);
        foreach (var sessionEvent in history)
        {
            next = sessionEvent.Seq + 1;
            yield return new RemoteSessionEvent(sessionId, sessionEvent);
        }
        await foreach (var item in buffer.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!item.Reset && item.Event is { } current && current.Seq < next)
                continue;
            next = item.Reset ? 0 : (item.Event?.Seq ?? next - 1) + 1;
            yield return item;
        }
    }

    private static RemoteSessionSummary Describe(IAgent agent)
    {
        var header = agent.Session.Header;
        return new RemoteSessionSummary(header.Id.Value, header.Cwd ?? "", agent.Status.ToString(), header.CreatedAt,
            header.Title, Summarize(agent.Session), LastEventTime(agent.Session));
    }

    private static RemoteSessionSummary Describe(SessionHeader header)
        => new(header.Id.Value, header.Cwd ?? "", "Idle", header.CreatedAt, header.Title, null, header.CreatedAt);

    /** 最近一条非空的用户消息; 只看常驻尾窗, 否则窗口化恢复的会话会为一句摘要把整份日志按页读回来。 */
    private static string? Summarize(Session session)
    {
        for (var seq = session.Seq - 1; seq >= session.ResidentFrom; seq--)
        {
            if (session.EventAt(seq)?.Data is not UserMessagePayload { Message.Source: UserMessageSource } user)
                continue;
            var text = string.Concat(user.Message.Content.OfType<TextBlock>().Select(block => block.Text));
            if (text.Length > 0)
                return text.Length > 120 ? text[..120] : text;
        }
        return null;
    }

    /** 最后事件时间; 播种会话(fork)的最后一条事件带着父会话的旧时间, 所以以创建时间为下界。 */
    private static long LastEventTime(Session session)
    {
        // 只取最后一条: SnapshotEvents() 在窗口会话里等于把整份日志读回来。
        var seq = session.Seq;
        var last = seq > 0 ? session.EventAt(seq - 1) : null;
        return last is null ? session.Header.CreatedAt : Math.Max(last.Time, session.Header.CreatedAt);
    }


    private IReadOnlyList<SessionEvent> ReadPersisted(SessionId id, long fromSeq, long? totalEvents)
    {
        using var handle = runtime.Persistence?.Open(id, SessionAccess.Read);
        if (handle is null)
            return [];
        // 带上长度: 句柄据此只解压在范围内命中帧(索引可信时是几十毫秒), 而不是把整份日志展开成事件数组。
        // 总量未知(实现不数事件)时才退回无长度的整份读。
        return totalEvents is { } total ? handle.Read(fromSeq, total - fromSeq) : handle.Read(fromSeq);
    }
}
