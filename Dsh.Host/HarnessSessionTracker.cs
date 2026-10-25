using System.Collections.Concurrent;
using Dsh.Core;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Host;

/**
 * 把 harness 的 agent 事件同步到会话登记表: 只跟踪经本 host 创建或恢复的根会话。
 * agent 状态给出 Running / Idle, 会话事件给出活动时间与标题, agent 释放即 Exited; WaitingForUser 由登记表按未决请求数叠加。
 */
internal sealed class HarnessSessionTracker : IDisposable
{
    private readonly HostSessionRegistry _registry;
    private readonly HostPendingDecisions _pending;
    private readonly ConcurrentDictionary<string, byte> _tracked = new(StringComparer.Ordinal);
    private readonly Func<bool>[] _subscriptions;

    public HarnessSessionTracker(Context ctx, HostSessionRegistry registry, HostPendingDecisions pending)
    {
        _registry = registry;
        _pending = pending;
        var global = new EventOptions { Global = true };
        _subscriptions =
        [
            ctx.On<AgentStatusNotification>(OnStatus, global),
            ctx.On<SessionEventNotification>(OnSessionEvent, global),
            ctx.On<AgentDisposedNotification>(OnDisposed, global),
        ];
    }

    public void Track(IAgent agent)
    {
        var header = agent.Session.Header;
        var events = agent.Session.SnapshotEvents();
        _tracked[agent.Id.Value] = 0;
        _registry.Register(new RemoteSessionSummary(
            agent.Id.Value,
            HostSessionKinds.Harness,
            HostAgentKinds.Dsharp,
            header.Cwd,
            header.Title,
            StatusOf(agent.Status),
            header.CreatedAt,
            events.Count == 0 ? header.CreatedAt : events[^1].Time,
            0,
            null));
    }

    /** 回到 Idle 的 agent 不再有工具在等回答: 取消它自己发起、还没人回答的请求(例如被中断的那一轮留下的)。 */
    private void OnStatus(AgentStatusNotification notification)
    {
        var id = notification.Agent.Id.Value;
        if (notification.Status == AgentStatus.Idle)
            _pending.CancelScope(id);
        if (_tracked.ContainsKey(id))
            _registry.SetStatus(id, StatusOf(notification.Status));
    }

    private void OnSessionEvent(SessionEventNotification notification)
    {
        var id = notification.Session.Id.Value;
        if (!_tracked.ContainsKey(id))
            return;
        _registry.Touch(id, notification.Event.Time);
        _registry.SetTitle(id, notification.Session.Header.Title);
    }

    private void OnDisposed(AgentDisposedNotification notification)
    {
        var id = notification.Agent.Id.Value;
        _pending.CancelScope(id);
        if (_tracked.TryRemove(id, out _))
            _registry.MarkExited(id, null);
    }

    private static string StatusOf(AgentStatus status)
        => status == AgentStatus.Running ? HostSessionStatuses.Running : HostSessionStatuses.Idle;

    /** host 退出时它托管的 harness 会话随之结束。 */
    public void Dispose()
    {
        foreach (var unsubscribe in _subscriptions)
            unsubscribe();
        foreach (var id in _tracked.Keys)
            _registry.MarkExited(id, null);
        _tracked.Clear();
    }
}

