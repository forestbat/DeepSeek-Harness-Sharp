using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Persistence.Database;

/** 写盘窗口: 条数、明文字节与时间三个上限, 先到者提交。 */
public sealed record DatabaseWriteBatch(int MaxEvents, int MaxPlaintextBytes, TimeSpan Interval)
{
    public static DatabaseWriteBatch Default { get; } = new(
        MaxEvents: JsonlWriteBatch.DefaultMaxEvents,
        MaxPlaintextBytes: JsonlWriteBatch.DefaultMaxPlaintextBytes,
        Interval: TimeSpan.FromMilliseconds(5));
}

/**
 * 数据库后端的句柄: 缓冲与发号由 `BufferedSessionHandle` 统一实现, 这里只有库特有的部分 ——
 * 把一批事件编成帧交给提交道、首次落库时建 `log` 行、读走 SQL、关闭时把写句柄让出来。
 */
internal sealed class DatabaseSessionHandle(
    DatabaseSessionPersistence storage,
    SessionId id,
    SessionHeader header,
    SessionAccess access,
    long generation,
    long cursor,
    bool materialized,
    long inheritedEventCount) : BufferedSessionHandle(id, header, access, cursor, materialized, inheritedEventCount)
{
    protected override int MaxBufferedEvents => storage.WriteBatch.MaxEvents;

    protected override TimeSpan DrainInterval => storage.WriteBatch.Interval;

    protected override long StoredCount() => storage.StoredEventCount(Id);

    protected override IReadOnlyList<SessionEvent> ReadStored(long offset, long? length)
        => storage.ReadEvents(Id, offset, length);

    protected override void CommitEvents(IReadOnlyList<SessionEvent> events)
    {
        var frames = SessionFrameWriter.Encode(events, storage.PackChunks, storage.WriteBatch.MaxPlaintextBytes, compress: true);
        storage.Commit(new CommitBatch
        {
            SessionId = Id,
            Generation = generation,
            FirstSeq = BufferedFrom,
            Frames = frames,
            EventCount = events.Count,
            ByteCount = frames.Sum(frame => (long)frame.Content.Length),
            Init = Materialized ? null : storage.BuildLogRow(Header, InheritedEventCount, generation),
        });
    }

    /** 只有头的会话(空会话改名)也要在库里留一行: 否则列表里看不到它。 */
    protected override void CommitHeaderOnly()
        => storage.Commit(new CommitBatch
        {
            SessionId = Id,
            Generation = generation,
            FirstSeq = Cursor,
            Frames = [],
            EventCount = 0,
            Init = Materialized ? null : storage.BuildLogRow(Header, InheritedEventCount, generation),
        });

    protected override void SaveTail(IReadOnlyList<SessionEvent> events, Exception error)
        => storage.SaveUnsavedTail(Header, InheritedEventCount, events, error);

    protected override void ReleaseResources() => storage.ReleaseHandle(this);
}


