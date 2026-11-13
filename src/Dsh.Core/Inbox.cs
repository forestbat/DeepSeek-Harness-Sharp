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
    private readonly Session _session;
    private readonly InboxNotifications _notifications;
    private readonly List<UserMessage> _nextTurn = [];
    private readonly List<UserMessage> _nextStep = [];
    private readonly Action<Session, long> _onTruncated;

    public Inbox(Session session, InboxNotifications notifications)
    {
        _session = session;
        _notifications = notifications;
        Rebuild();
        _onTruncated = (_, _) => RebuildAfterTruncation();
        session.Truncated += _onTruncated;
    }

    public void Dispose() => _session.Truncated -= _onTruncated;

    private void Rebuild()
    {
        _nextTurn.Clear();
        _nextStep.Clear();
        foreach (var sessionEvent in _session.OwnEvents())
        {
            if (sessionEvent.Data is InboxSplicePayload splice)
                Apply(splice);
        }
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
        var pendingBefore = _nextTurn.Concat(_nextStep).Select(message => message.Id).ToHashSet();
        Rebuild();
        PurgeStale(InboxTargets.NextTurn, _nextTurn, pendingBefore);
        PurgeStale(InboxTargets.NextStep, _nextStep, pendingBefore);
    }

    private void PurgeStale(string target, List<UserMessage> inbox, HashSet<MessageId> pendingBefore)
    {
        for (var index = inbox.Count - 1; index >= 0; index--)
        {
            if (!pendingBefore.Contains(inbox[index].Id))
                Splice(target, index, 1, []);
        }
    }

    public IReadOnlyList<UserMessage> NextTurn => _nextTurn;

    public IReadOnlyList<UserMessage> NextStep => _nextStep;

    public bool HasPending => _nextTurn.Count > 0 || _nextStep.Count > 0;

    /**
     * 清空收件箱。keep 指定要留下的消息 —— 取消时会用它保住"后台任务已完成"这类通知:
     * 那正是用户按停之后最需要看到的东西, 清掉等于把已经跑完的成果一起丢掉。
     */
    public void Clear(Func<UserMessage, bool>? keep = null)
    {
        Splice(InboxTargets.NextStep, 0, _nextStep.Count, keep is null ? [] : [.. _nextStep.Where(keep)]);
        Splice(InboxTargets.NextTurn, 0, _nextTurn.Count, keep is null ? [] : [.. _nextTurn.Where(keep)]);
    }

    internal List<UserMessage> Claim(string target, int turn)
    {
        var claimed = Mutate(InboxTargets.NextStep, 0, _nextStep.Count, [], false);
        if (target == InboxTargets.NextTurn)
            claimed.AddRange(Mutate(InboxTargets.NextTurn, 0, 1, [], false));
        foreach (var message in claimed)
            _notifications.Claimed?.Invoke(message, turn);
        return claimed;
    }

    public void Append(string target, UserMessage message) => Splice(target, ListFor(target).Count, 0, [message]);

    public void Prepend(string target, UserMessage message) => Splice(target, 0, 0, [message]);

    public bool Replace(MessageId messageId, UserMessage newMessage)
    {
        var location = Locate(messageId);
        if (location is null)
            return false;
        Splice(location.Value.Target, location.Value.Index, 1, [newMessage]);
        return true;
    }

    public bool Remove(MessageId messageId)
    {
        var location = Locate(messageId);
        if (location is null)
            return false;
        Splice(location.Value.Target, location.Value.Index, 1, []);
        return true;
    }

    public IReadOnlyList<UserMessage> Splice(string target, long start, long deleteCount, IReadOnlyList<UserMessage> inserted)
        => Mutate(target, start, deleteCount, inserted, true);

    private (string Target, int Index)? Locate(MessageId messageId)
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

    private List<UserMessage> Mutate(string target, long start, long deleteCount, IReadOnlyList<UserMessage> inserted, bool discardRemoved)
    {
        var inbox = ListFor(target);
        var actualStart = start < 0 ? Math.Max(inbox.Count + (int)start, 0) : (int)Math.Min(start, inbox.Count);
        var actualDeleteCount = (int)Math.Min(Math.Max(deleteCount, 0), inbox.Count - actualStart);
        if (actualDeleteCount == 0 && inserted.Count == 0)
            return [];
        var outcome = discardRemoved && actualDeleteCount > 0 ? "canceled" : null;
        var splice = new InboxSplicePayload(
            target,
            actualStart,
            actualDeleteCount == 0 ? null : actualDeleteCount,
            inserted,
            outcome);
        Validate(splice);
        _session.Append(splice);
        var removed = inbox.GetRange(actualStart, actualDeleteCount);
        inbox.RemoveRange(actualStart, actualDeleteCount);
        inbox.InsertRange(actualStart, splice.Inserted);
        if (discardRemoved)
        {
            foreach (var message in removed)
                _notifications.Discarded?.Invoke(message);
        }
        foreach (var message in splice.Inserted)
            _notifications.Inserted?.Invoke(message);
        return removed;
    }

    private void Apply(InboxSplicePayload splice)
    {
        Validate(splice);
        var inbox = ListFor(splice.Target);
        inbox.RemoveRange((int)splice.Start, (int)(splice.RemovedCount ?? 0));
        inbox.InsertRange((int)splice.Start, splice.Inserted);
    }

    private void Validate(InboxSplicePayload splice)
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
