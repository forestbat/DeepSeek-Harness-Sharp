using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Runtime.Events;

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
            if (agent.Session.Header.IsSubagent || agent.Session.SnapshotEvents().Count == 0)
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

    /** 返回整段历史; 分页由 RemoteHostServer 按帧预算切, 所以这里的 TotalEventCount 就是整段条数。 */
    public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken)
    {
        var id = SessionId.Create(sessionId);
        var approvals = runtime.App.Ctx.Get<ApprovalService>(ApprovalService.ServiceName, false);
        if (runtime.Store.Get(id) is { } session)
        {
            var live = session.SnapshotEvents();
            return Task.FromResult(new RemoteSessionSnapshot(session.Header, live, live.Count,
                approvals?.EffectivePolicy(session).ToString()));
        }
        using var handle = runtime.Persistence?.Open(id, SessionAccess.Read)
            ?? throw new InvalidOperationException($"unknown session: {sessionId}");
        var events = handle.Read();
        // 只为一个策略值重建整篇派生状态(surface 折叠等)不划算: 策略就藏在事件流里。
        return Task.FromResult(new RemoteSessionSnapshot(handle.Header, events, events.Count,
            approvals?.EffectivePolicy(events).ToString()));
    }

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
            buffer.Writer.TryWrite(new RemoteSessionEvent(sessionId, Reset: true));
            foreach (var item in notification.Session.SnapshotEvents())
                buffer.Writer.TryWrite(new RemoteSessionEvent(sessionId, item));
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
            var history = session is not null
                ? session.SnapshotEvents(reset ? 0 : fromSeq)
                // 客户端通常已经拿到整段快照再来订阅: 起点追平条数时没必要为一片空白解析整份日志。
                : !reset && stored?.EventCount is { } total && fromSeq >= total
                    ? []
                    : ReadPersisted(id, reset ? 0 : fromSeq);
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

    private static string? Summarize(Session session)
    {
        var events = session.SnapshotEvents();
        for (var index = events.Count - 1; index >= 0; index--)
        {
            if (events[index].Data is not UserMessagePayload { Message.Source: UserMessageSource } user)
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
        var events = session.SnapshotEvents();
        return events.Count == 0 ? session.Header.CreatedAt : Math.Max(events[^1].Time, session.Header.CreatedAt);
    }


    private IReadOnlyList<SessionEvent> ReadPersisted(SessionId id, long fromSeq)
    {
        using var handle = runtime.Persistence?.Open(id, SessionAccess.Read);
        return handle is null ? [] : handle.Read(fromSeq);
    }
}
