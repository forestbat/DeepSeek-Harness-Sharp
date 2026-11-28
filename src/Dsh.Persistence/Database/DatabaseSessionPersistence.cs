using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Persistence.Database;

/** 会话日志库里的一行(读出来的头与计数)。 */
internal sealed record LogRow(
    SessionHeader Header,
    long InheritedEventCount,
    long Generation,
    long DurabilitySeq,
    long FrameBytes,
    long Revision);

/**
 * 数据库后端: 一个中心一个本地库, 会话日志是"一行头 + 若干行帧"。
 *
 * 与 JSONL 后端的分工是同一份契约下的两种实现: 这里的事务替掉了写票、帧索引自愈、尾巴修复与尾锁
 * (`plans/会话持久化-数据库后端方案-2026-11-27.md` §4), 序号仍由句柄发、仍以库里的条数为准。
 */
public sealed partial class DatabaseSessionPersistence : ISessionPersistence, IDisposable
{
    private const string SelectRow =
        "SELECT generation, durability_seq, frame_bytes, revision, header_frame FROM log WHERE session_id = ?1";

    private readonly string _dbPath;
    private readonly EventStoreOptions _storeOptions;
    private readonly object _gate = new();
    private readonly Dictionary<SessionId, PendingSession> _pending = [];
    private readonly Dictionary<SessionId, DatabaseSessionHandle> _writers = [];
    private readonly HashSet<DatabaseSessionHandle> _handles = [];
    private EventStore? _store;
    private int _pendingCounter;
    private bool _disposed;

    public DatabaseSessionPersistence(
        string databasePath,
        EventStoreOptions? storeOptions = null,
        DatabaseWriteBatch? writeBatch = null,
        bool packChunks = true,
        Action<string>? warn = null)
    {
        _dbPath = Path.GetFullPath(databasePath);
        _storeOptions = storeOptions ?? EventStoreOptions.Default;
        WriteBatch = writeBatch ?? DatabaseWriteBatch.Default;
        PackChunks = packChunks;
        Warn = warn;
    }

    internal DatabaseWriteBatch WriteBatch { get; }

    internal bool PackChunks { get; }

    internal Action<string>? Warn { get; }

    /** 本节点身份: 库首次打开时生成并记住。 */
    public string NodeId => Store.NodeId;

    public string DatabasePath => _dbPath;

    /** 引擎版本(原生库自带): 只用于启动日志与自检。 */
    public string EngineVersion => Store.EngineVersion;

    private EventStore Store
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_store is { } existing)
                    return existing;
                _store = EventStore.Open(_dbPath, _storeOptions, Warn);
                // 上次写不进去而另存的那几段帧: 先收回来, 再让调用方看到"少了一截"的会话。
                ReplayPendingEscrow();
                return _store;
            }
        }
    }

    /** 提交次数与批次数(打点用)。 */
    internal (long Commits, long Batches) CommitCounters => (Store.Lanes.CommitCount, Store.Lanes.BatchCount);

    internal void Commit(CommitBatch batch)
    {
        Store.Commit(batch);
        // 首次落库之后会话就不再是"内存待写"了: 不摘掉这一项, 之后再 Open(Write) 会一直判成"已被占用"。
        if (batch.Init is not null)
        {
            lock (_gate)
                _pending.Remove(batch.SessionId);
        }
    }

    /** 首段落库时那一行的内容: 头字段展开成列, 头帧与 JSONL 后端同构。 */
    internal LogRowInit BuildLogRow(SessionHeader header, long inheritedEventCount, long generation) => new(
        OwnerNode: Store.NodeId,
        Generation: generation,
        HeaderFrame: SessionRowCodec.EncodeHeaderFrame(header, inheritedEventCount));

    public ISessionHandle Create(SessionHeader header, long? inheritedEventCount = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        header.Validate();
        SessionLogHeader.ValidateSeedCut(header.IsSeeded, inheritedEventCount);
        lock (_gate)
        {
            if (_pending.ContainsKey(header.Id) || _writers.ContainsKey(header.Id) || RowExists(header.Id))
                throw new SessionAlreadyExistsException(header.Id);
            var cut = inheritedEventCount ?? 0;
            _pending[header.Id] = new PendingSession(header, $"memory:{++_pendingCounter}", cut);
            var handle = Adopt(new DatabaseSessionHandle(this, header.Id, header, SessionAccess.Write,
                generation: 0, cursor: 0, materialized: false, inheritedEventCount: cut));
            _writers[header.Id] = handle;
            return handle;
        }
    }

    public ISessionHandle Open(SessionId id, SessionAccess access)
    {
        if (access == SessionAccess.Write)
        {
            lock (_gate)
            {
                if (_writers.ContainsKey(id))
                    throw new SessionAlreadyOwnedException(id);
                if (_pending.TryGetValue(id, out var pending))
                    throw new SessionAlreadyOwnedException(id);
                var row = ReadRow(id) ?? throw new SessionPersistenceNotFoundException(id);
                var handle = Adopt(new DatabaseSessionHandle(this, id, row.Header, SessionAccess.Write,
                    row.Generation, row.DurabilitySeq, materialized: true, inheritedEventCount: row.InheritedEventCount));
                _writers[id] = handle;
                return handle;
            }
        }
        lock (_gate)
        {
            if (_pending.TryGetValue(id, out var pending))
                return Adopt(new DatabaseSessionHandle(this, id, pending.Header, SessionAccess.Read,
                    generation: 0, cursor: 0, materialized: false, inheritedEventCount: pending.InheritedEventCount));
        }
        var stored = ReadRow(id) ?? throw new SessionPersistenceNotFoundException(id);
        return Adopt(new DatabaseSessionHandle(this, id, stored.Header, SessionAccess.Read,
            stored.Generation, stored.DurabilitySeq, materialized: true, inheritedEventCount: stored.InheritedEventCount));
    }

    public SessionPersistenceSnapshot? Stat(SessionId id)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(id, out var pending))
                return Snapshot(pending.Header, pending.Revision, 0, null, pending.InheritedEventCount);
        }
        return ReadRow(id) is { } row
            ? Snapshot(row.Header, EventStore.FormatRevision(row.Revision, row.DurabilitySeq), row.DurabilitySeq, row.FrameBytes, row.InheritedEventCount)
            : null;
    }

    public IReadOnlyList<SessionPersistenceSnapshot> List()
    {
        var snapshots = new List<SessionPersistenceSnapshot>();
        using (var lease = Store.RentReader())
        {
            lease.Connection.Query(
                "SELECT generation, durability_seq, frame_bytes, revision, header_frame FROM log",
                null,
                statement => snapshots.Add(ToSnapshot(statement)));
        }
        // 排序在内存里做: 时间就在头帧里, 不为它多存一列。
        snapshots.Sort((left, right) => right.Header.CreatedAt.CompareTo(left.Header.CreatedAt));
        List<PendingSession> pending;
        lock (_gate)
            pending = [.. _pending.Values];
        var listed = snapshots.Select(snapshot => snapshot.Header.Id).ToHashSet();
        foreach (var entry in pending)
        {
            if (!listed.Contains(entry.Header.Id))
                snapshots.Add(Snapshot(entry.Header, entry.Revision, 0, null, entry.InheritedEventCount));
        }
        return snapshots;
    }

    public long StoredEventCount(SessionId id) => ReadRow(id)?.DurabilitySeq ?? 0;

    internal IReadOnlyList<SessionEvent> ReadEvents(SessionId id, long offset, long? length)
    {
        var row = ReadRow(id);
        if (row is null)
        {
            lock (_gate)
            {
                if (_pending.ContainsKey(id))
                    return [];
            }
            throw new SessionPersistenceNotFoundException(id);
        }
        var total = row.DurabilitySeq;
        var start = Math.Clamp(offset, 0, total);
        var end = length is { } count ? Math.Min(total, start + count) : total;
        if (end <= start)
            return [];
        var events = new List<SessionEvent>(end <= int.MaxValue ? (int)(end - start) : 0);
        using (var lease = Store.RentReader())
        {
            // 一条查询边走边解: 帧字节在引擎的结果缓冲上直接解压, 不落成托管数组。
            // 早先按 64 帧一条 SQL 分批, 百万帧的日志就是一万六千次解析/执行; 而"一次性把全部帧读进 List"
            // 又会把整份日志再拷一遍。行回调里解码两头都避开: 查询一次, 内存里只留解出来的事件。
            // 范围谓词必须是 `first_seq` 上的纯区间: 写成 `first_seq + event_count > ?` 时索引用不上,
            // 每读一页都退化成扫完整张 frame 表(百万行级, 实测占热开 CPU 的 66%)。
            // "覆盖起点的那一帧"由子查询按主键倒着取一条, 同样走索引。
            lease.Connection.Query(
                "SELECT first_seq, event_count, bytes FROM frame WHERE session_id = ?1 AND first_seq >= " +
                "(SELECT COALESCE(MAX(first_seq), 0) FROM frame WHERE session_id = ?1 AND first_seq <= ?3) " +
                "AND first_seq < ?2 ORDER BY first_seq",
                statement =>
                {
                    statement.BindText(1, id.Value);
                    statement.BindInt(2, end);
                    statement.BindInt(3, start);
                },
                statement =>
                {
                    var decoded = SessionLogFrameCodec.DecodeFrame(
                        statement.BytesSpan(2), statement.Int(0), checked((int)statement.Int(1)));
                    SessionRowCodec.AppendInRange(decoded, start, end, events);
                });
        }
        return events;
    }

    public void Rename(SessionId id, string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var row = ReadRow(id);
        if (row is null)
        {
            lock (_gate)
            {
                if (_pending.TryGetValue(id, out var pending))
                {
                    var renamed = pending.Header with { Title = title };
                    _pending[id] = pending with { Header = renamed };
                    UpdateOpenHandles(id, renamed);
                    return;
                }
            }
            throw new SessionPersistenceNotFoundException(id);
        }
        var header = row.Header with { Title = title };
        var headerFrame = SessionRowCodec.EncodeHeaderFrame(header, row.InheritedEventCount);
        Store.Admin(connection => connection.Execute(
            "UPDATE log SET header_frame = ?1 WHERE session_id = ?2",
            statement =>
            {
                statement.BindBlob(1, headerFrame);
                statement.BindText(2, id.Value);
            }));
        lock (_gate)
        {
            if (_pending.TryGetValue(id, out var pending))
                _pending[id] = pending with { Header = header };
        }
        UpdateOpenHandles(id, header);
    }

    public void Delete(SessionId id)
    {
        List<DatabaseSessionHandle> handles;
        lock (_gate)
            handles = [.. _handles.Where(handle => handle.Id == id)];
        foreach (var handle in handles)
            handle.Close();
        lock (_gate)
        {
            _pending.Remove(id);
            _writers.Remove(id);
        }
        Store.Admin(connection =>
        {
            connection.Execute("DELETE FROM frame WHERE session_id = ?1", statement => statement.BindText(1, id.Value));
            connection.Execute("DELETE FROM surface_fold WHERE session_id = ?1", statement => statement.BindText(1, id.Value));
            connection.Execute("DELETE FROM log WHERE session_id = ?1", statement => statement.BindText(1, id.Value));
        });
    }

    public SessionWindow? TryOpenWindow(SessionId id, int tailEvents)
    {
        if (tailEvents <= 0)
            return null;
        var row = ReadRow(id);
        if (row is null || row.DurabilitySeq <= tailEvents)
            return null;
        var total = row.DurabilitySeq;
        var boundary = total - tailEvents;
        // 先用索引定位"尾窗之外的最后一帧"(一条 SQL 的定位, 与帧数无关),
        // 再从那一帧往后取 —— 现场有一份会话是 104 万帧, 按帧数递减扫描是扫不过来的。
        long? residentFrom = null;
        using (var lease = Store.RentReader())
        {
            var connection = lease.Connection;
            connection.Query(
                "SELECT first_seq FROM frame WHERE session_id = ?1 AND first_seq <= ?2 ORDER BY first_seq DESC LIMIT 1",
                statement =>
                {
                    statement.BindText(1, id.Value);
                    statement.BindInt(2, boundary);
                },
                statement => residentFrom = statement.Int(0));
            residentFrom ??= 0;
        }
        // 尾窗起点就是日志开头: 没有可省的部分, 交给调用方走整份打开。
        if (residentFrom == 0)
            return null;
        var start = residentFrom.Value;
        var frames = new List<(long FirstSeq, int EventCount, byte[] Bytes)>();
        using (var lease = Store.RentReader())
        {
            lease.Connection.Query(
                "SELECT first_seq, event_count, bytes FROM frame WHERE session_id = ?1 AND first_seq >= ?2 ORDER BY first_seq",
                statement =>
                {
                    statement.BindText(1, id.Value);
                    statement.BindInt(2, start);
                },
                statement => frames.Add((statement.Int(0), checked((int)statement.Int(1)), statement.Bytes(2) ?? [])));
        }
        if (frames.Count == 0)
            return null;
        var resident = SessionRowCodec.DecodeFrames(frames, frames[0].FirstSeq, total);
        if (resident.Count == 0 || resident[0].Seq != frames[0].FirstSeq || resident.Count < tailEvents)
            return null;
        return new SessionWindow(frames[0].FirstSeq, total, resident,
            (offset, count) => ReadEvents(id, offset, count));
    }

    /** 按区间交出帧视图: 切片、导出、导入、交接全部建立在它上面(与 JSONL 后端同一份契约)。 */
    public SessionLogFrames? TryReadLogFrames(SessionId id)
    {
        var row = ReadRow(id);
        if (row is null || row.DurabilitySeq <= 0)
            return null;
        var headerFrame = ReadHeaderFrame(id);
        if (headerFrame is null)
            return null;
        return new SessionLogFrames(row.Header, row.InheritedEventCount, headerFrame, row.DurabilitySeq,
            (fromEvent, maxEvents) => Covering(id, fromEvent, maxEvents));
    }

    public SessionLogSlice? TryReadLogSlice(SessionId id, long fromEvent, int maxEvents)
        => TryReadLogFrames(id)?.ToSlice(fromEvent, maxEvents);

    /** 库里按事件区间取帧: 帧边界只落在行边界上, 所以交出的是"覆盖该区间的整帧"。 */
    private IEnumerable<SessionLogFrames.Frame> Covering(SessionId id, long fromEvent, int maxEvents)
    {
        var total = StoredEventCount(id);
        var start = Math.Clamp(fromEvent, 0, total);
        var count = maxEvents > 0 ? Math.Min(maxEvents, total - start) : total - start;
        if (count <= 0)
            return [];
        var frames = new List<SessionLogFrames.Frame>();
        using (var lease = Store.RentReader())
        {
            lease.Connection.Query(
                "SELECT first_seq, event_count, bytes, min_replace_start FROM frame WHERE session_id = ?1 AND first_seq >= " +
                "(SELECT COALESCE(MAX(first_seq), 0) FROM frame WHERE session_id = ?1 AND first_seq <= ?3) " +
                "AND first_seq < ?2 ORDER BY first_seq",
                statement =>
                {
                    statement.BindText(1, id.Value);
                    statement.BindInt(2, start + count);
                    statement.BindInt(3, start);
                },
                statement => frames.Add(new SessionLogFrames.Frame(
                    statement.Int(0), checked((int)statement.Int(1)), statement.Bytes(2) ?? [])
                {
                    MinReplaceStart = statement.Int(3),
                }));
        }
        return frames;
    }

    public SurfaceFoldSnapshot? TryLoadSurfaceFold(SessionId id)
    {
        var row = ReadRow(id);
        if (row is null)
            return null;
        SurfaceFoldSnapshot? snapshot = null;
        using var lease = Store.RentReader();
        lease.Connection.Query(
            "SELECT event_count, replace_generation, nodes, fold_meta FROM surface_fold WHERE session_id = ?1 AND generation = ?2",
            statement =>
            {
                statement.BindText(1, id.Value);
                statement.BindInt(2, row.Generation);
            },
            statement =>
            {
                try
                {
                    var nodes = SurfaceFoldCodec.DecodeAll(statement.Bytes(2) ?? []);
                    // 尾部信息(request/header 与 request/context 的折叠值)随检查点一起存: 尾窗里常常没有这两条,
                    // 没有它们就得从日志尾部往前扫一遍(单页 3~5s), 窗口化的收益会被吃掉。
                    var (header, context) = statement.Bytes(3) is { Length: > 0 } meta
                        ? SurfaceFoldCodec.DecodeMeta(meta)
                        : (null, null);
                    var written = statement.Int(0);
                    snapshot = new SurfaceFoldSnapshot(nodes, written, written,
                        checked((int)statement.Int(1)), header, context);
                }
                catch (FormatException)
                {
                    snapshot = null;
                }
            });
        // 条数是写检查点时的折叠进度: 库只追加, 现在的条数不小于它; 变短(回退/重导)就让缓存作废。
        if (snapshot is not { } loaded)
            return null;
        if (!loaded.Fits(row.DurabilitySeq, row.DurabilitySeq))
        {
            // 检查点比日志还长(回退/重导过): 直接作废, 这次整份打开并重写一份。
            Warn?.Invoke($"session \"{id}\": 折叠检查点与日志对不上(fold={loaded.TotalEvents}/{loaded.Nodes.Count} 节点, "
                + $"log={row.DurabilitySeq}), 这次整份打开");
            return null;
        }
        return loaded;
    }

    public void SaveSurfaceFold(SessionId id, SurfaceFoldSnapshot fold)
    {
        var row = ReadRow(id);
        if (row is null || fold.Nodes.Count == 0)
            return;
        // 检查点声明的条数不得超过**已落盘**条数: 恢复出来的会话常带着一条尚未落盘的内存事件
        // (比如 session/end-seed 标记), 写成 fold.TotalEvents 会让读侧的 Fits 判死(声明 > 日志)。
        // 节点若指向未落盘的事件, 这份检查点就整份不写。
        // 检查点只能描述**已落盘**的事件: 恢复出来的会话常带着尚未落盘的内存事件(合成收尾、end-seed 标记),
        // 折进去的节点会指向日志之外。这类检查点整份不写 —— 宿主落盘尾部之后的下一份才有效。
        var durable = row.DurabilitySeq;
        if (fold.TotalEvents > durable || fold.Nodes.Any(node => node >= durable))
            return;
        var nodes = SurfaceFoldCodec.EncodeNodes(fold.Nodes);
        var meta = SurfaceFoldCodec.EncodeMeta(fold);
        Store.Admin(connection => connection.Execute(
            "INSERT OR REPLACE INTO surface_fold(session_id, generation, event_count, replace_generation, nodes, fold_meta, updated_at) " +
            "VALUES(?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            statement =>
            {
                statement.BindText(1, id.Value);
                statement.BindInt(2, row.Generation);
                // 记的是**折叠进度**, 不是日志总长: 部分检查点读回来从折到的地方接着折。
                statement.BindInt(3, fold.TotalEvents);
                statement.BindInt(4, fold.ReplaceGeneration);
                statement.BindBlob(5, nodes);
                // 与 JSONL 的 `.surface` 第三帧同一份编码: 同一份检查点在两个后端之间可以互换。
                statement.BindBlob(6, meta);
                statement.BindInt(7, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }));
    }

    public void DropSurfaceFold(SessionId id)
        => Store.Admin(connection => connection.Execute(
            "DELETE FROM surface_fold WHERE session_id = ?1", statement => statement.BindText(1, id.Value)));

    /** 备份与整库搬运: `VACUUM INTO` 出一份可独立打开的副本(MVCC 下直接可用)。 */
    public void Backup(string target) => Store.Backup(target);

    /** 关闭全部写句柄: 剩下的缓冲在这里补写进库。 */
    public void Flush()
    {
        List<DatabaseSessionHandle> writers;
        lock (_gate)
            writers = [.. _writers.Values];
        List<Exception>? errors = null;
        foreach (var writer in writers)
        {
            try
            {
                writer.Flush();
            }
            catch (SessionHandleClosedException)
            {
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }
        if (errors is not null)
            throw new AggregateException("session database flush failed", errors);
    }

    public void Dispose()
    {
        List<DatabaseSessionHandle> handles;
        lock (_gate)
            handles = [.. _handles];
        List<Exception>? errors = null;
        foreach (var handle in handles)
        {
            try
            {
                handle.Close();
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }
        EventStore? store;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            store = _store;
            _store = null;
        }
        store?.Dispose();
        if (errors is not null)
            throw new AggregateException("session database dispose failed", errors);
    }

    internal DatabaseSessionHandle Adopt(DatabaseSessionHandle handle)
    {
        _handles.Add(handle);
        return handle;
    }

    internal void ReleaseHandle(DatabaseSessionHandle handle)
    {
        lock (_gate)
        {
            _handles.Remove(handle);
            if (_writers.TryGetValue(handle.Id, out var writer) && ReferenceEquals(writer, handle))
                _writers.Remove(handle.Id);
        }
    }

    /**
     * 落盘失败时把这一段**编成帧**另存一份(头帧 + 待提交帧, 与导出同构): 事件是用户刚说的话, 不能丢。
     *
     * 存成帧而不是逐条 JSON 行, 是为了下次打开能**机械重放**回库(见 `ReplayPendingEscrow`):
     * 不需要重新编码, 字节就是当时想提交的那一份。
     */
    internal void SaveUnsavedTail(SessionHeader header, long inheritedEventCount, IReadOnlyList<SessionEvent> events, Exception error)
    {
        if (events.Count == 0)
            return;
        try
        {
            var frames = SessionFrameWriter.Encode(events, PackChunks, WriteBatch.MaxPlaintextBytes, compress: true);
            var path = $"{_dbPath}.pending-{Environment.ProcessId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.log";
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(SessionRowCodec.EncodeHeaderFrame(header, inheritedEventCount));
                foreach (var frame in frames)
                    stream.Write(frame.Content);
                stream.Flush(true);
            }
            Warn?.Invoke($"session \"{header.Id}\": {events.Count} unsaved event(s) were escrowed at {path} "
                + $"because the session database rejected the batch ({error.GetType().Name}: {error.Message}); "
                + "the next open of this database replays it");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Warn?.Invoke($"session \"{header.Id}\": {events.Count} unsaved event(s) were LOST — "
                + $"the recovery copy could not be written either ({failure.Message})");
        }
    }

    private void UpdateOpenHandles(SessionId id, SessionHeader header)
    {
        List<DatabaseSessionHandle> handles;
        lock (_gate)
            handles = [.. _handles.Where(handle => handle.Id == id)];
        foreach (var handle in handles)
            handle.UpdateHeader(header);
    }

    private bool RowExists(SessionId id) => ReadRow(id) is not null;

    private LogRow? ReadRow(SessionId id)
    {
        LogRow? row = null;
        using var lease = Store.RentReader();
        lease.Connection.Query(SelectRow, statement => statement.BindText(1, id.Value),
            statement => row = ToRow(statement));
        return row;
    }

    private byte[]? ReadHeaderFrame(SessionId id)
    {
        byte[]? frame = null;
        using var lease = Store.RentReader();
        lease.Connection.Query("SELECT header_frame FROM log WHERE session_id = ?1",
            statement => statement.BindText(1, id.Value),
            statement => frame = statement.Bytes(0));
        return frame;
    }

    private static LogRow ToRow(TursoStatement statement)
    {
        var metadata = SessionRowCodec.DecodeHeaderFrame(statement.Bytes(4)
            ?? throw new SessionPersistenceCorruptionException("the session row has no header frame"));
        return new LogRow(metadata.Meta, metadata.InheritedEventCount, statement.Int(0), statement.Int(1), statement.Int(2), statement.Int(3));
    }

    private static SessionPersistenceSnapshot ToSnapshot(TursoStatement statement)
    {
        var row = ToRow(statement);
        return Snapshot(row.Header, EventStore.FormatRevision(row.Revision, row.DurabilitySeq),
            row.DurabilitySeq, row.FrameBytes, row.InheritedEventCount);
    }

    private static SessionPersistenceSnapshot Snapshot(SessionHeader header, string revision, long? eventCount, long? sizeBytes, long inherited)
        => new()
        {
            Header = header,
            Revision = revision,
            EventCount = eventCount,
            SizeBytes = sizeBytes,
            InheritedEventCount = inherited,
        };

    private sealed record PendingSession(SessionHeader Header, string Revision, long InheritedEventCount);
}






















