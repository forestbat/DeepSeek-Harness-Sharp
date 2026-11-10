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
            // 种子(fork)里的最后一条用户消息带的是父会话的旧时间: 活动时间不能早于本会话的创建时间。
            Math.Max(events.LastOrDefault(IsUserActivity)?.Time ?? header.CreatedAt, header.CreatedAt),
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
        // 只有用户自己发起的一轮(提交消息/开始回合)才算"这个会话被用过"。
        // LLM 的流式输出、工具调用同样会推事件; 若按每个事件刷新活动时间, 两个会话并发输出时,
        // "最近会话"排序会随消息到达速度来回换序, 消息刷得越快切换越频繁。
        if (IsUserActivity(notification.Event))
            _registry.Touch(id, notification.Event.Time);
        // 标题由首轮结束后的摘要更新产生, 与"谁在用"无关, 任何事件都要同步。
        _registry.SetTitle(id, notification.Session.Header.Title);
    }

    private static bool IsUserActivity(SessionEvent sessionEvent)
        => sessionEvent.Type is SessionEventTypes.UserMessage or SessionEventTypes.TurnStart;

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

