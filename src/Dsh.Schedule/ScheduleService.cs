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

    static ScheduleService() => ScheduleChangePayload.RegisterCodec();

    public ScheduleService(Context ctx, TimeProvider? time = null) : base(ctx, ServiceName)
    {
        _time = time ?? TimeProvider.System;
        var global = new EventOptions { Global = true };
        _subscriptions =
        [
            ctx.On<AgentCreatedNotification>(notification => Arm(notification.Agent), global),
            ctx.On<AgentDisposedNotification>(notification => Disarm(notification.Agent.Session), global),
            ctx.On<SessionTruncateNotification>(notification => Rearm(notification.Session), global),
        ];
        foreach (var agent in ctx.Get<AgentRegistry>(AgentRegistry.ServiceName, false)?.List() ?? [])
            Arm(agent);
    }

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
            foreach (var arming in _armed.Values)
                arming.Timer.Dispose();
            _armed.Clear();
        }
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

    private void Disarm(Session session)
    {
        lock (_gate)
        {
            if (_armed.Remove(session, out var arming))
                arming.Timer.Dispose();
        }
    }

    private void Rearm(Session session)
    {
        lock (_gate)
        {
            if (!_armed.TryGetValue(session, out var arming))
                return;
            var active = ScheduleLedger.Of(session).Active;
            var delay = active.Count == 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromTicks(Math.Clamp((active.Min(record => record.Rule.ScheduledAt) - Now).Ticks, 0, MaxTimerDelay.Ticks));
            arming.Timer.Change(delay, Timeout.InfiniteTimeSpan);
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
