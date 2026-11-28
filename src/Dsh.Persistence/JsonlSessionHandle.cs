using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Persistence;

internal sealed class StorageHandleState
{
    public required long Cursor { get; set; }
    public required bool Materialized { get; set; }
    public long? TornTruncateTo { get; set; }
    public List<SessionEvent>? RecoveredTail { get; set; }
    public required long InheritedEventCount { get; init; }
    public List<SessionEvent>? Primed { get; set; }

    /** Bytes written by this handle; null until the log is materialized. */
    public long? WrittenLength { get; set; }
}

/**
 * JSONL 后端的句柄: 缓冲与发号由 `BufferedSessionHandle` 统一实现, 这里只有文件特有的部分 ——
 * 修尾、与文件长度对账、丢半帧、窗口化开写时预先读进内存的那一段、以及写票的领/还。
 */
internal sealed class JsonlSessionHandle(
    JsonlSessionPersistence storage,
    SessionId id,
    SessionHeader header,
    SessionAccess access,
    StorageHandleState state) : BufferedSessionHandle(id, header, access, state.Cursor, state.Materialized, state.InheritedEventCount)
{
    /** 上一次落盘之后观测到的日志长度: 与文件对账用。 */
    private long _observedLength;

    protected override int MaxBufferedEvents => storage.WriteBatch.MaxEvents;

    protected override TimeSpan DrainInterval => storage.WriteBatch.Interval;

    protected override long? PrimedCount => state.Primed?.Count;

    protected override long StoredCount() => storage.StoredEventCount(Id);

    protected override void OnBufferedChanged()
    {
        state.Cursor = Cursor;
        state.Primed = null;
        _observedLength = Cursor;
    }

    protected override bool TryReadPrimed(long offset, long? length, out IReadOnlyList<SessionEvent> events)
    {
        if (state.Primed is { } primed)
        {
            _observedLength = Math.Max(_observedLength, primed.Count);
            events = Slice(primed, offset, length ?? long.MaxValue);
            return true;
        }
        events = [];
        return false;
    }

    protected override IReadOnlyList<SessionEvent> ReadStored(long offset, long? length)
    {
        if (Access == SessionAccess.Write && !state.Materialized)
            return [];
        var path = storage.ResolveLog(Id);
        if (path is null)
        {
            if (storage.HasPendingSession(Id)) return [];
            throw new SessionPersistenceNotFoundException(Id);
        }

        // 要一个窗口就走缺页重读: 解压命中帧即可, 不必把整份日志展开成事件数组。
        if (length is { } window && window < int.MaxValue && offset > 0
            && storage.TryReadWindow(path, Id, offset, (int)window) is { } windowed)
        {
            // 窗口已按 [offset, offset + window) 过滤过, 这里不能再按绝对 offset 切一遍。
            // 也不更新 _observedLength: 窗口不代表日志总量, 那会让后续全量读误判"日志缩水"。
            return windowed.Events;
        }

        var events = storage.ReadStoredLog(path, Id).Events;
        if (events.Count < _observedLength)
            throw new SessionPersistenceCorruptionException(
                $"session \"{Id}\": stored log shrank below a previously observed prefix ({events.Count} < {_observedLength})");
        _observedLength = events.Count;
        return Slice(events, offset, length ?? long.MaxValue);
    }

    protected override void PrepareCommit()
    {
        state.Cursor = Cursor;
        state.Materialized = Materialized;
        // 尾巴上有无法归属的字节: 现在就修掉。宿主可能连续开着几天, "等下次打开再修"等于这几天一条都不写。
        if (state.Materialized && !storage.IsLogTailClean(Id))
        {
            long repaired;
            try
            {
                repaired = storage.RepairDamagedTail(Id);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
            {
                // 修不动不代表事件可以丢: 按"这次先别写"处理, 缓冲留着, 下一次落盘再试。
                throw new SessionWriteBusyException(Environment.ProcessId,
                    $"the log tail could not be repaired right now ({error.GetType().Name}); retrying on the next write");
            }
            if (repaired != Cursor && !TryRebaseLocked(repaired))
                TryAlignToDiskLocked();
            state.WrittenLength = null;
        }
        // 日志在上一批之后变过: 在拿尾锁**之前**先把内存对齐好 —— 持锁时读整份日志会撞上自己占住的那段字节。
        if (state.Materialized && state.WrittenLength is { } knownLength
            && storage.LogLength(Id) is { } currentLength && currentLength != knownLength)
        {
            var stored = storage.StoredEventCount(Id);
            // 盘上已经越过了"我上次落盘的位置": 说明有人占用了我们发出去的号, 只能重编号。
            if (stored > DurableCount)
            {
                if (storage.IsLogTailClean(Id))
                    TryRebaseLocked(stored);
            }
            else if (stored < Cursor)
            {
                // 我们自己的某一批没落成: 整段补写。尾巴脏时不动文件, 交给追加路径报损伤。
                TryAlignToDiskLocked();
            }
        }
        if (state.TornTruncateTo is { } truncateTo)
        {
            storage.TruncateTornTail(Header, truncateTo);
            state.TornTruncateTo = null;
            state.WrittenLength = truncateTo;
            _observedLength = Cursor;
        }
        if (state.RecoveredTail is { } recovered)
        {
            if (recovered.Count > 0)
                storage.PersistBatch(Header, recovered, state);
            state.RecoveredTail = null;
            state.Materialized = true;
        }
    }

    protected override void CommitEvents(IReadOnlyList<SessionEvent> events)
    {
        storage.PersistBatch(Header, events, state);
        state.Materialized = true;
    }

    protected override void CommitHeaderOnly()
    {
        storage.PersistHeader(Header, state);
        state.Materialized = true;
    }

    protected override void AfterCommit()
    {
        state.Cursor = Cursor;
        // 票随落盘前进: 没有未落盘事件时它会把自己删掉, 空闲进程不占发号权。
        storage.MarkDurable(Header, DurableCount);
    }

    protected override long NextSeqLocked() => storage.NextSeq(Header, 1, Cursor);

    protected override void SaveTail(IReadOnlyList<SessionEvent> events, Exception error)
        => storage.SaveUnsavedTail(Header, events, error);

    /** 重新对齐/重编号之后: 已知长度作废, 追加路径要重新量一次文件。 */
    protected override void OnAlignedToStorage() => state.WrittenLength = null;

    protected override void OnRebased() => state.WrittenLength = null;

    /** 读回别的写者写进去的那一段: 不走"日志缩水"判定(那会让重编号在读到更短的区间时误报)。 */
    public override IReadOnlyList<SessionEvent> ReadRange(long from, long to)
        => storage.ReadStoredRange(Id, from, to);

    protected override void ReleaseResources()
    {
        if (Access == SessionAccess.Write)
            storage.ReleaseTicket(Header);
        storage.ReleaseHandle(this, state.Materialized);
    }

    /** 名字/标题变化要重写头帧: 期间不能有正在缓冲的批次。 */
    internal void WhileDrained(Func<long?, long> action)
    {
        DrainFromStorageChange();
        var rewrittenLength = action(state.WrittenLength);
        if (state.TornTruncateTo is { } truncateTo && state.WrittenLength is { } previousLength)
            state.TornTruncateTo = truncateTo + rewrittenLength - previousLength;
        state.WrittenLength = rewrittenLength;
    }

    /** 子系统(改名/重写头帧)要求先把缓冲落盘: 与 Flush 同一条路径。 */
    private void DrainFromStorageChange()
    {
        try
        {
            Flush();
        }
        catch (SessionHandleClosedException)
        {
        }
    }


    private static IReadOnlyList<SessionEvent> Slice(List<SessionEvent> events, long offset, long limit)
    {
        if (offset >= events.Count || limit == 0) return [];
        var count = (int)Math.Min(limit, events.Count - offset);
        return events.GetRange((int)offset, count);
    }
}

