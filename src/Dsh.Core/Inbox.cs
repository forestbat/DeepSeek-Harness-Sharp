using Dsh.Llm;

namespace Dsh.Core;

public static class InboxTargets
{
    public const string NextTurn = "next-turn";
    public const string NextStep = "next-step";
}

public sealed class InboxNotifications
{
    public Action<UserMessage>? Inserted { get; init; }
    public Action<UserMessage>? Discarded { get; init; }
    public Action<UserMessage, int>? Claimed { get; init; }
}

public sealed class Inbox : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Session _session;
    private readonly InboxNotifications _notifications;
    private readonly List<UserMessage> _nextTurn = [];
    private readonly List<UserMessage> _nextStep = [];
    private readonly Dictionary<MessageId, MessageAttribution> _attributions = [];

    private readonly Action<Session, long> _onReverted;

    public Inbox(Session session, InboxNotifications notifications)
    {
        _session = session;
        _notifications = notifications;
        Rebuild();
        // 回退是只追加的: 日志没变短, 但"入队 → 取走"里被回退掉的是"取走", 按日志重放就会把已发的那条
        // 又当成待发, 所以按日志整份重放一次(见 RebuildAfterTruncation)。

        _onReverted = (_, _) => RebuildAfterTruncation();
        session.Reverted += _onReverted;
    }

    public void Dispose()
    {

        _session.Reverted -= _onReverted;
    }

    private void Rebuild()
    {
        lock (_gate)
            RebuildLocked();
    }

    private void RebuildLocked()
    {
        _nextTurn.Clear();
        _nextStep.Clear();
        _attributions.Clear();
        // 收件箱只关心"现在还有什么没被取走"。窗口化恢复的会话里, 前缀要按页读回来(383MB 会话实测 24s),
        // 所以只重放常驻段; 那时指向窗口之前的 splice 无从校验 —— 放不进当前列表就跳过, 那不是日志损坏。
        var windowed = _session.ResidentFrom > 0;
        var from = Math.Max(_session.InheritedEventCount, _session.ResidentFrom);
        foreach (var sessionEvent in _session.SnapshotEvents(from))
        {
            if (sessionEvent.Data is not InboxSplicePayload splice)
                continue;
            // 回退只追加: 被回退区间里的 splice 已经不生效, 跳过后面的清理才不会把撤回的那条又摆回来。
            if (_session.IsReverted(sessionEvent.Seq))
                continue;
            if (windowed && !FitsLocked(splice))
                continue;
            ApplyLocked(splice);
        }
    }

    private bool FitsLocked(InboxSplicePayload splice)
    {
        var inbox = ListFor(splice.Target);
        return splice.Start >= 0 && splice.Start + (splice.RemovedCount ?? 0) <= inbox.Count;
    }

    /**
     * 撤回把日志改短时要重放, 否则内存与日志分叉; 但**单靠重放不够**:
     * 发一条消息的日志顺序是「入队 → TurnStart → 取走 → 用户消息」, 撤回截到 TurnStart 之前,
     * 于是保住了「入队」却丢掉了「取走」—— 按日志重放会把已经发出去的那条重新当成待发。
     * 判据是截断前的待发集合: 重建后才出现、截断前不在待发里的消息, 只可能来自被撤回的回合。
     * 找到之后要把清理写回日志, 否则下次从磁盘恢复(新 Inbox 没有截断前快照)又会复活。
     */
    private void RebuildAfterTruncation()
    {
        List<(bool Discarded, UserMessage Message)> events = [];
        lock (_gate)
        {
            var pendingBefore = _nextTurn.Concat(_nextStep).Select(message => message.Id).ToHashSet();
            RebuildLocked();
            PurgeStaleLocked(InboxTargets.NextTurn, _nextTurn, pendingBefore, events);
            PurgeStaleLocked(InboxTargets.NextStep, _nextStep, pendingBefore, events);
        }
        EmitSpliceEvents(events);
    }

    private void PurgeStaleLocked(
        string target,
        List<UserMessage> inbox,
        HashSet<MessageId> pendingBefore,
        List<(bool Discarded, UserMessage Message)> events)
    {
        for (var index = inbox.Count - 1; index >= 0; index--)
        {
            if (!pendingBefore.Contains(inbox[index].Id))
                MutateLocked(target, index, 1, [], null, discardRemoved: true, events);
        }
    }

    public IReadOnlyList<UserMessage> NextTurn
    {
        get
        {
            lock (_gate)
                return [.. _nextTurn];
        }
    }

    public IReadOnlyList<UserMessage> NextStep
    {
        get
        {
            lock (_gate)
                return [.. _nextStep];
        }
    }

    public bool HasPending
    {
        get
        {
            lock (_gate)
                return _nextTurn.Count > 0 || _nextStep.Count > 0;
        }
    }

    public MessageAttribution? PeekAttribution(MessageId messageId)
    {
        lock (_gate)
            return _attributions.GetValueOrDefault(messageId);
    }

    public MessageAttribution? TakeAttribution(MessageId messageId)
    {
        lock (_gate)
            return _attributions.Remove(messageId, out var attribution) ? attribution : null;
    }

    /**
     * 清空收件箱。keep 指定要留下的消息 —— 取消时会用它保住"后台任务已完成"这类通知:
     * 那正是用户按停之后最需要看到的东西, 清掉等于把已经跑完的成果一起丢掉。
     */
    public void Clear(Func<UserMessage, bool>? keep = null)
    {
        List<(bool Discarded, UserMessage Message)> events = [];
        lock (_gate)
        {
            ClearTargetLocked(InboxTargets.NextStep, _nextStep, keep, events);
            ClearTargetLocked(InboxTargets.NextTurn, _nextTurn, keep, events);
        }
        EmitSpliceEvents(events);
    }

    private void ClearTargetLocked(
        string target,
        List<UserMessage> inbox,
        Func<UserMessage, bool>? keep,
        List<(bool Discarded, UserMessage Message)> events)
    {
        var kept = keep is null ? [] : inbox.Where(keep).ToList();
        var keptAttributions = CollectAttributionsLocked(kept);
        MutateLocked(target, 0, inbox.Count, kept, keptAttributions, discardRemoved: true, events);
    }

    private Dictionary<MessageId, MessageAttribution>? CollectAttributionsLocked(IReadOnlyList<UserMessage> messages)
    {
        if (messages.Count == 0 || _attributions.Count == 0)
            return null;
        Dictionary<MessageId, MessageAttribution>? collected = null;
        foreach (var message in messages)
        {
            if (_attributions.TryGetValue(message.Id, out var attribution))
                (collected ??= [])[message.Id] = attribution;
        }
        return collected;
    }

    internal List<UserMessage> Claim(string target, int turn)
    {
        List<UserMessage> claimed;
        lock (_gate)
        {
            claimed = MutateLocked(InboxTargets.NextStep, 0, _nextStep.Count, [], null, discardRemoved: false, events: null);
            if (target == InboxTargets.NextTurn)
                claimed.AddRange(MutateLocked(InboxTargets.NextTurn, 0, 1, [], null, discardRemoved: false, events: null));
        }
        foreach (var message in claimed)
            _notifications.Claimed?.Invoke(message, turn);
        return claimed;
    }

    public void Append(
        string target,
        UserMessage message,
        IReadOnlyDictionary<MessageId, MessageAttribution>? attributions = null)
        => Splice(target, int.MaxValue, 0, [message], attributions);

    public void Prepend(
        string target,
        UserMessage message,
        IReadOnlyDictionary<MessageId, MessageAttribution>? attributions = null)
        => Splice(target, 0, 0, [message], attributions);

    public bool Replace(
        MessageId messageId,
        UserMessage newMessage,
        IReadOnlyDictionary<MessageId, MessageAttribution>? attributions = null)
    {
        List<(bool Discarded, UserMessage Message)> events = [];
        lock (_gate)
        {
            var location = LocateLocked(messageId);
            if (location is null)
                return false;
            MutateLocked(location.Value.Target, location.Value.Index, 1, [newMessage], attributions, discardRemoved: true, events);
        }
        EmitSpliceEvents(events);
        return true;
    }

    public bool Remove(MessageId messageId)
    {
        List<(bool Discarded, UserMessage Message)> events = [];
        lock (_gate)
        {
            var location = LocateLocked(messageId);
            if (location is null)
                return false;
            MutateLocked(location.Value.Target, location.Value.Index, 1, [], null, discardRemoved: true, events);
        }
        EmitSpliceEvents(events);
        return true;
    }

    public IReadOnlyList<UserMessage> Splice(
        string target,
        long start,
        long deleteCount,
        IReadOnlyList<UserMessage> inserted,
        IReadOnlyDictionary<MessageId, MessageAttribution>? attributions = null)
    {
        List<(bool Discarded, UserMessage Message)> events = [];
        List<UserMessage> removed;
        lock (_gate)
            removed = MutateLocked(target, start, deleteCount, inserted, attributions, discardRemoved: true, events);
        EmitSpliceEvents(events);
        return removed;
    }

    private (string Target, int Index)? LocateLocked(MessageId messageId)
    {
        foreach (var target in new[] { InboxTargets.NextTurn, InboxTargets.NextStep })
        {
            var index = ListFor(target).FindIndex(message => message.Id == messageId);
            if (index >= 0)
                return (target, index);
        }
        return null;
    }

    private List<UserMessage> ListFor(string target)
        => target switch
        {
            InboxTargets.NextTurn => _nextTurn,
            InboxTargets.NextStep => _nextStep,
            _ => throw new ArgumentException($"unknown inbox target \"{target}\""),
        };

    private List<UserMessage> MutateLocked(
        string target,
        long start,
        long deleteCount,
        IReadOnlyList<UserMessage> inserted,
        IReadOnlyDictionary<MessageId, MessageAttribution>? attributions,
        bool discardRemoved,
        List<(bool Discarded, UserMessage Message)>? events)
    {
        var inbox = ListFor(target);
        var actualStart = start < 0 ? Math.Max(inbox.Count + (int)start, 0) : (int)Math.Min(start, inbox.Count);
        var actualDeleteCount = (int)Math.Min(Math.Max(deleteCount, 0), inbox.Count - actualStart);
        if (actualDeleteCount == 0 && inserted.Count == 0)
            return [];
        var outcome = discardRemoved && actualDeleteCount > 0 ? "canceled" : null;
        var serializedAttributions = BuildSerializedAttributions(inserted, attributions);
        var splice = new InboxSplicePayload(
            target,
            actualStart,
            actualDeleteCount == 0 ? null : actualDeleteCount,
            inserted,
            outcome,
            serializedAttributions);
        ValidateLocked(splice);
        _session.Append(splice);
        var removed = inbox.GetRange(actualStart, actualDeleteCount);
        inbox.RemoveRange(actualStart, actualDeleteCount);
        inbox.InsertRange(actualStart, splice.Inserted);
        UpdateAttributionsLocked(removed, splice.Inserted, splice.Attributions, removeRemovedAttributions: discardRemoved);
        if (events is not null)
        {
            if (discardRemoved)
            {
                foreach (var message in removed)
                    events.Add((true, message));
            }
            foreach (var message in splice.Inserted)
                events.Add((false, message));
        }
        return removed;
    }

    private void EmitSpliceEvents(List<(bool Discarded, UserMessage Message)> events)
    {
        foreach (var (discarded, message) in events)
        {
            if (discarded)
                _notifications.Discarded?.Invoke(message);
            else
                _notifications.Inserted?.Invoke(message);
        }
    }

    private void ApplyLocked(InboxSplicePayload splice)
    {
        ValidateLocked(splice);
        var inbox = ListFor(splice.Target);
        var start = (int)splice.Start;
        var removedCount = (int)(splice.RemovedCount ?? 0);
        var removed = inbox.GetRange(start, removedCount);
        inbox.RemoveRange(start, removedCount);
        inbox.InsertRange(start, splice.Inserted);
        UpdateAttributionsLocked(removed, splice.Inserted, splice.Attributions, removeRemovedAttributions: true);
    }

    private static Dictionary<string, MessageAttribution>? BuildSerializedAttributions(
        IReadOnlyList<UserMessage> inserted,
        IReadOnlyDictionary<MessageId, MessageAttribution>? attributions)
    {
        if (inserted.Count == 0 || attributions is null || attributions.Count == 0)
            return null;
        Dictionary<string, MessageAttribution>? result = null;
        foreach (var message in inserted)
        {
            if (attributions.TryGetValue(message.Id, out var attribution))
                (result ??= new Dictionary<string, MessageAttribution>(StringComparer.Ordinal))[message.Id.Value] = attribution;
        }
        return result;
    }

    private void UpdateAttributionsLocked(
        List<UserMessage> removed,
        IReadOnlyList<UserMessage> inserted,
        IReadOnlyDictionary<string, MessageAttribution>? serializedAttributions,
        bool removeRemovedAttributions)
    {
        if (removeRemovedAttributions && removed.Count > 0)
        {
            HashSet<MessageId>? insertedIds = inserted.Count > 0
                ? [.. inserted.Select(message => message.Id)]
                : null;
            foreach (var message in removed)
            {
                if (insertedIds is null || !insertedIds.Contains(message.Id))
                    _attributions.Remove(message.Id);
            }
        }
        if (serializedAttributions is { Count: > 0 })
        {
            foreach (var message in inserted)
            {
                if (serializedAttributions.TryGetValue(message.Id.Value, out var attribution))
                    _attributions[message.Id] = attribution;
            }
        }
    }

    private void ValidateLocked(InboxSplicePayload splice)
    {
        var inbox = ListFor(splice.Target);
        var removedCount = splice.RemovedCount ?? 0;
        if (splice.Start < 0 || splice.Start > inbox.Count || removedCount < 0 || splice.Start + removedCount > inbox.Count)
            throw new InvalidOperationException("invalid inbox splice");
        var candidate = inbox.ToList();
        candidate.RemoveRange((int)splice.Start, (int)removedCount);
        candidate.InsertRange((int)splice.Start, splice.Inserted);
        var ids = new HashSet<MessageId>();
        var combined = splice.Target == InboxTargets.NextTurn
            ? candidate.Concat(_nextStep)
            : _nextTurn.Concat(candidate);
        foreach (var message in combined)
        {
            if (!ids.Add(message.Id))
                throw new InvalidOperationException($"message \"{message.Id}\" is already pending");
        }
    }
}
