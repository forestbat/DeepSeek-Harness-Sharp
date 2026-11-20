using Dsh.Core;

namespace Dsh.Schedule;

/** The reminders a session's own `schedule/change` events describe, in creation order. */
public sealed class ScheduleLedger
{
    private readonly List<ScheduleRecord> _active = [];
    private readonly HashSet<string> _ended = [];

    public IReadOnlyList<ScheduleRecord> Active => [.. _active];

    /**
     * 本会话自己的 schedule/change 事件描述的提醒。
     * 只看常驻段: 窗口化恢复的会话里往前扫到第一条事件等于把整份日志按页读回来(383MB 会话实测 24s);
     * 仍然有效的提醒每次派发都会再写一条 change, 所以尾部一段就足以还原"现在还有哪些"。
     */
    public static ScheduleLedger Of(Session session)
    {
        var ledger = new ScheduleLedger();
        foreach (var sessionEvent in session.SnapshotEvents(Math.Max(session.InheritedEventCount, session.ResidentFrom)))
        {
            if (sessionEvent.Data is ScheduleChangePayload payload)
                ledger.Apply(payload.Change);
        }
        return ledger;
    }

    public ScheduleRecord? Find(string id) => _active.Find(record => record.Id == id);

    public bool HasEnded(string id) => _ended.Contains(id);

    private void Apply(ScheduleChange change)
    {
        switch (change.Operation)
        {
            case ScheduleOperation.Create when change.Schedule is { } created:
                _active.Add(created);
                break;
            case ScheduleOperation.Update when change.Schedule is { } updated:
                Replace(updated.Id, _ => updated);
                break;
            case ScheduleOperation.Delete when change.Id is { } deleted:
                _active.RemoveAll(record => record.Id == deleted);
                _ended.Remove(deleted);
                break;
            case ScheduleOperation.Dispatch when change.Id is { } id && change.NextAt is { } next:
                Replace(id, record => record with { Rule = record.Rule with { ScheduledAt = next } });
                break;
            case ScheduleOperation.Dispatch when change.Id is { } id:
                if (_active.RemoveAll(record => record.Id == id) > 0)
                    _ended.Add(id);
                break;
        }
    }

    private void Replace(string id, Func<ScheduleRecord, ScheduleRecord> change)
    {
        var index = _active.FindIndex(record => record.Id == id);
        if (index >= 0)
            _active[index] = change(_active[index]);
    }
}
