using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Agent;

namespace Dsh.Schedule;

/**
 * Session-owned reminders.
 * The `schedule/change` events in a session's own log are the only state, so a resumed session gets its reminders back,
 * while a fork or a seeded child (whose copies are inherited events) starts without any.
 * Timers run only while the session has a live agent; a due reminder reaches that agent as a follow-up message.
 */
public sealed class ScheduleService : Service, IDisposable
{
    public const string ServiceName = "schedules";
    public const string MessagePlugin = "schedule";
    public const int MaxTitleLength = 120;

    private const string IdPrefix = "schedule-";

    /** Long waits are split so a far target never exceeds the platform timer range. */
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromDays(1);

    private sealed record Arming(IAgent Agent, ITimer Timer);

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<Session, Arming> _armed = [];
    private readonly IReadOnlyList<Func<bool>> _subscriptions;
    private readonly ITimer _indexTimer;
    private readonly SemaphoreSlim _wakeGate = new(1, 1);
    private bool _disposed;

    static ScheduleService() => ScheduleChangePayload.RegisterCodec();

    public ScheduleService(Context ctx, TimeProvider? time = null, string? wakeIndexPath = null) : base(ctx, ServiceName)
    {
        _time = time ?? TimeProvider.System;
        var resolvedIndexPath = wakeIndexPath
            ?? (DshPaths.FromContext(ctx) is { } paths ? Path.Combine(paths.Path("schedules"), "wake-index.json") : null);
        WakeIndex = new ScheduleWakeIndex(resolvedIndexPath);
        _indexTimer = _time.CreateTimer(_ => _ = FireDueIndexAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var global = new EventOptions { Global = true };
        _subscriptions =
        [
            ctx.On<AgentCreatedNotification>(notification => Arm(notification.Agent), global),
            ctx.On<AgentDisposedNotification>(notification => Disarm(notification.Agent), global),

        ];
        foreach (var agent in ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false)?.List() ?? [])
            Arm(agent);
        RearmIndex();
    }

    public ScheduleWakeIndex WakeIndex { get; }

    public DateTimeOffset Now => _time.GetUtcNow();

    public IReadOnlyList<ScheduleRecord> List(IAgent agent)
    {
        lock (_gate)
            return ScheduleLedger.Of(agent.Session).Active;
    }

    public ScheduleRecord Create(IAgent agent, string title, string prompt, ScheduleTiming timing)
    {
        var record = new ScheduleRecord(
            $"{IdPrefix}{Guid.NewGuid():N}",
            ValidTitle(title),
            ValidPrompt(prompt),
            ScheduleRules.Resolve(timing, Now));
        lock (_gate)
            Commit(agent.Session, new ScheduleChange(ScheduleOperation.Create, record));
        return record;
    }

    public ScheduleUpdateResult Update(IAgent agent, string id, string? title, string? prompt, ScheduleTiming? timing)
    {
        lock (_gate)
        {
            var ledger = ScheduleLedger.Of(agent.Session);
            if (ledger.Find(id) is not { } current)
                return new ScheduleUpdateResult(null, ledger.HasEnded(id) ? ScheduleErrorCodes.Ended : ScheduleErrorCodes.NotFound);
            var updated = current with
            {
                Title = title is null ? current.Title : ValidTitle(title),
                Prompt = prompt is null ? current.Prompt : ValidPrompt(prompt),
                Rule = timing is null ? current.Rule : ScheduleRules.Resolve(timing, Now),
            };
            Commit(agent.Session, new ScheduleChange(ScheduleOperation.Update, updated));
            return new ScheduleUpdateResult(updated, null);
        }
    }

    /** Removes an active or ended reminder; a reminder message already queued in the inbox stays queued. */
    public bool Delete(IAgent agent, string id)
    {
        lock (_gate)
        {
            var ledger = ScheduleLedger.Of(agent.Session);
            if (ledger.Find(id) is null && !ledger.HasEnded(id))
                return false;
            Commit(agent.Session, new ScheduleChange(ScheduleOperation.Delete, Id: id));
            return true;
        }
    }

    public void Dispose()
    {
        foreach (var unsubscribe in _subscriptions)
            unsubscribe();
        lock (_gate)
        {
            _disposed = true;
            _indexTimer.Dispose();
            foreach (var arming in _armed.Values)
                arming.Timer.Dispose();
            _armed.Clear();
        }
        _wakeGate.Dispose();
    }

    private static string ValidTitle(string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxTitleLength)
        {
            throw new HarnessException(
                $"title is required, non-empty after trimming, and at most {MaxTitleLength} characters",
                ScheduleErrorCodes.InvalidPrompt);
        }
        return trimmed;
    }

    private static string ValidPrompt(string prompt)
    {
        var trimmed = prompt.Trim();
        return trimmed.Length > 0
            ? trimmed
            : throw new HarnessException("prompt must be non-empty after trimming", ScheduleErrorCodes.InvalidPrompt);
    }

    private void Commit(Session session, ScheduleChange change)
    {
        session.Append(new ScheduleChangePayload(change));
        Rearm(session);
    }

    private void Arm(IAgent agent)
    {
        lock (_gate)
        {
            if (_armed.ContainsKey(agent.Session)
                || !ReferenceEquals(Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false)?.Get(agent.Id), agent))
                return;
            var session = agent.Session;
            var timer = _time.CreateTimer(_ => Fire(session), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _armed[session] = new Arming(agent, timer);
            Rearm(session);
        }
    }

    private void Disarm(IAgent agent)
    {
        var keepWakeEntry = false;
        lock (_gate)
        {
            if (_armed.Remove(agent.Session, out var arming))
                arming.Timer.Dispose();
            keepWakeEntry = ScheduleLedger.Of(agent.Session).Active.Count > 0
                && CanWakeOffline(agent.AgentId, agent.Id);
            if (!keepWakeEntry)
                WakeIndex.Remove(agent.AgentId, agent.Id);
        }
        if (keepWakeEntry)
            RearmIndex();
    }

    private void Rearm(Session session)
    {
        lock (_gate)
        {
            if (!_armed.TryGetValue(session, out var arming))
                return;
            var active = ScheduleLedger.Of(session).Active;
            if (active.Count == 0)
            {
                WakeIndex.Remove(arming.Agent.AgentId, arming.Agent.Id);
                arming.Timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }
            var earliestAt = active.Min(record => record.Rule.ScheduledAt);
            WakeIndex.Upsert(new ScheduleWakeEntry(
                arming.Agent.AgentId,
                arming.Agent.Id,
                earliestAt.ToUnixTimeMilliseconds(),
                session.WorkingDirectory ?? session.Header.Cwd));
            var delay = TimeSpan.FromTicks(Math.Clamp((earliestAt - Now).Ticks, 0, MaxTimerDelay.Ticks));
            arming.Timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private bool CanWakeOffline(AgentId agentId, SessionId sessionId)
    {
        if (Ctx.Get<IPersistentAgentManager>(IPersistentAgentManager.ServiceName, false)?.Get(agentId) is { State: not AgentLifecycleState.Disposed })
            return true;
        return Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, false)?.Stat(sessionId) is not null;
    }

    private void RearmIndex()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var agents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
            var next = WakeIndex.List()
                .FirstOrDefault(entry => agents?.Get(entry.SessionId) is null && agents?.GetByAgent(entry.AgentId) is null);
            if (next is null)
            {
                _indexTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }
            var dueAt = DateTimeOffset.FromUnixTimeMilliseconds(next.DueAtMs);
            var delay = TimeSpan.FromTicks(Math.Clamp((dueAt - Now).Ticks, 0, MaxTimerDelay.Ticks));
            _indexTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    internal async Task FireDueIndexAsync()
    {
        try
        {
            await _wakeGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        try
        {
            if (_disposed)
                return;
            var dueEntries = WakeIndex.DueAt(Now.ToUnixTimeMilliseconds());
            foreach (var entry in dueEntries)
            {
                if (_disposed)
                    return;
                var agents = Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false);
                if (agents?.Get(entry.SessionId) is not null || agents?.GetByAgent(entry.AgentId) is not null)
                    continue;

                IAgent? woken = null;
                try
                {
                    var manager = Ctx.Get<IPersistentAgentManager>(IPersistentAgentManager.ServiceName, false);
                    if (manager?.Get(entry.AgentId) is { State: not AgentLifecycleState.Disposed })
                    {
                        woken = await manager.WakeAsync(entry.AgentId).ConfigureAwait(false);
                    }
                    else if (agents is not null
                        && Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, false)?.Stat(entry.SessionId) is not null)
                    {
                        var resumed = await agents.Resume(new ResumeAgentOptions(entry.SessionId, AgentId: entry.AgentId))
                            .ConfigureAwait(false);
                        woken = resumed.Agent;
                    }
                    else
                    {
                        WakeIndex.Remove(entry.AgentId, entry.SessionId);
                    }
                }
                catch (Exception error)
                {
                    Ctx.LoggerFor("schedules").Warn(
                        $"failed to wake agent \"{entry.AgentId}\" for scheduled reminder: {error.Message}");
                }

                if (woken is not null)
                    Fire(woken.Session);
            }
        }
        finally
        {
            RearmIndex();
            try
            {
                _wakeGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void Fire(Session session)
    {
        IAgent agent;
        IReadOnlyList<UserMessage> messages;
        lock (_gate)
        {
            if (!_armed.TryGetValue(session, out var arming))
                return;
            agent = arming.Agent;
            var now = Now;
            var due = ScheduleLedger.Of(session).Active.Where(record => record.Rule.ScheduledAt <= now).ToList();
            messages = Dispatch(session, due, now);
            Rearm(session);
        }
        foreach (var message in messages)
            agent.Followup(message);
    }

    /** Records each due occurrence before its message is queued, so the turn it starts already sees the advanced reminders. */
    private static List<UserMessage> Dispatch(Session session, IReadOnlyList<ScheduleRecord> due, DateTimeOffset now)
    {
        var messages = new List<UserMessage>();
        var recurring = new List<(ScheduleRecord Record, DateTimeOffset OccurrenceAt)>();
        foreach (var record in due)
        {
            var occurrence = ScheduleRules.Occur(record.Rule, now);
            session.Append(new ScheduleChangePayload(new ScheduleChange(
                ScheduleOperation.Dispatch, Id: record.Id, OccurrenceAt: occurrence.OccurrenceAt, NextAt: occurrence.NextAt)));
            if (record.Rule.IsRecurring)
                recurring.Add((record, occurrence.OccurrenceAt));
            else
                messages.Add(ScheduleFraming.Reminder(record, occurrence.OccurrenceAt));
        }
        if (recurring.Count > 0)
            messages.Add(ScheduleFraming.RecurringBatch(recurring));
        return messages;
    }
}
