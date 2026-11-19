using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Persistence;

public sealed class JsonlSessionPersistence : ISessionPersistence, IDisposable
{
    private const int FirstLineChunkSize = 8192;
    private const string BackendName = "session-persistence-jsonl";
    private const string ArtifactKind = "jsonl";

    /**
     * 读日志一律放行写入与删除: 会话活着时定时落盘会从另一线程追加, Windows 上只共享读的读取会和追加流互相拒绝(CI 上 IOException)。
     * 带上 Delete, 截断与改标题用临时文件 File.Move 覆盖日志时也不会被正在读的句柄挡住。
     */
    private const FileShare LiveLogReadShare = FileShare.ReadWrite | FileShare.Delete;

    private readonly string _root;
    private readonly bool _packChunks;
    private readonly JsonlCompression _compression;
    private readonly object _trackerGate = new();
    private readonly Dictionary<SessionId, JsonlSessionHandle?> _writers = [];
    /** 每个写句柄一份跨进程写租约: 别的进程只能显式接管, 不能悄悄追加。 */
    private readonly Dictionary<SessionId, SessionLease> _leases = [];
    private readonly Dictionary<SessionId, PendingSession> _pending = [];
    private readonly HashSet<JsonlSessionHandle> _openHandles = [];

    /**
     * 已解析的日志, 按文件版本号命中。
     * 一份长日志解析要解压整份文件再逐行解析, 手机滚动看历史时会反复请求同一条会话的相邻片段;
     * 文件没变就没有理由每次都重来一遍。
     *
     * 缓存按事件数估字节并设上限: 同一进程里被打开过的会话可能有几十条, 无上限时它们会把整机内存吃光。
     * 淘汰从最久未命中的一条开始, 当前活跃会话总在末位, 不会被自己挤掉。
     */
    private const long MaxCachedParsedBytes = 256L * 1024 * 1024;
    private const long EstimatedBytesPerEvent = 256;

    private readonly Dictionary<string, (string Revision, StoredLog Log)> _parsed = new(StringComparer.Ordinal);
    private readonly List<string> _parsedOrder = [];
    private readonly object _parsedGate = new();
    /** 每个日志一份帧视图(切帧只需要读一遍文件, 不解压), 活会话追加时只补扫新增段。 */
    private readonly Dictionary<string, SessionLogWindowReader> _windowReaders = new(StringComparer.Ordinal);
    private readonly object _windowGate = new();
    private int _pendingCounter;
    private readonly Lazy<bool> _rootEncodingCheck;
    /** 读日志发现损伤时的告警出口: 由插件注入 logger, 让"这次打开是降级的"在日志里可见。 */
    private readonly Action<string>? _warn;

    internal JsonlWriteBatch WriteBatch { get; }

    private sealed record PendingSession(SessionHeader Header, string Revision, long InheritedEventCount);

    private sealed record ParsedLog(SessionHeader Meta, List<SessionEvent> Events, long? TornTruncateTo, List<SessionEvent> RecoveredTail, long InheritedEventCount, LogDamage Damage, List<FrameEntry>? Frames = null);

    internal sealed record StoredLog(SessionHeader Meta, List<SessionEvent> Events, long? TornTruncateTo, List<SessionEvent> RecoveredTail, long InheritedEventCount, string Revision, LogDamage Damage);

    public JsonlSessionPersistence(
        string root,
        bool packChunks = true,
        JsonlCompression compression = JsonlCompression.Zstd,
        JsonlWriteBatch? writeBatch = null,
        Action<string>? warn = null)
    {
        _warn = warn;
        WriteBatch = writeBatch ?? JsonlWriteBatch.Default;
        if (WriteBatch.MaxEvents < 1 || WriteBatch.Interval <= TimeSpan.Zero || WriteBatch.MaxPlaintextBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(writeBatch),
                "write batch needs a positive event limit, a positive plaintext byte budget and a positive interval");
        _root = Path.GetFullPath(root);
        _packChunks = packChunks;
        _compression = compression;
        if (File.Exists(_root)) throw new IOException($"session root \"{_root}\" exists and is not a directory");
        if (Directory.Exists(_root)) Directory.EnumerateFileSystemEntries(_root).GetEnumerator().Dispose();
        _rootEncodingCheck = new Lazy<bool>(() => { CheckRootEncoding(); return true; });
    }

    public ISessionHandle Create(SessionHeader header, long? inheritedEventCount = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        header.Validate();
        SessionLogHeader.ValidateSeedCut(header.IsSeeded, inheritedEventCount);
        EnsureRootEncoding();
        lock (_trackerGate)
        {
            if (_pending.ContainsKey(header.Id) || _writers.ContainsKey(header.Id) || FindLog(header.Id) is not null)
                throw new SessionAlreadyExistsException(header.Id);
        }
        // 租约的文件重试不放在 _trackerGate 里: 抢租约可能要等另一个进程的短锁, 不能因此卡住所有会话。
        var lease = AcquireLease(header.Id, header.Cwd);
        lock (_trackerGate)
        {
            if (_pending.ContainsKey(header.Id) || _writers.ContainsKey(header.Id))
            {
                lease.Dispose();
                throw new SessionAlreadyExistsException(header.Id);
            }
            _writers[header.Id] = null;
            _leases[header.Id] = lease;
            var cut = inheritedEventCount ?? 0;
            _pending[header.Id] = new PendingSession(header, $"memory:{BackendName}:{++_pendingCounter}", cut);
            return AdoptLocked(new JsonlSessionHandle(this, header.Id, header, SessionAccess.Write,
                new StorageHandleState { Cursor = 0, Materialized = false, InheritedEventCount = cut }));
        }
    }

    public ISessionHandle Open(SessionId id, SessionAccess access)
    {
        EnsureRootEncoding();
        if (access == SessionAccess.Read)
        {
            lock (_trackerGate)
            {
                if (_pending.TryGetValue(id, out var pending))
                    return AdoptLocked(new JsonlSessionHandle(this, id, pending.Header, SessionAccess.Read,
                        new StorageHandleState { Cursor = 0, Materialized = false, InheritedEventCount = pending.InheritedEventCount }));
            }
            var snapshot = Stat(id);
            if (snapshot is null)
            {
                var stored = RequireStoredLog(id);
                lock (_trackerGate)
                    return AdoptLocked(new JsonlSessionHandle(this, id, stored.Meta, SessionAccess.Read,
                        new StorageHandleState { Cursor = 0, Materialized = true, InheritedEventCount = stored.InheritedEventCount }));
            }
            AssertVersion(snapshot.Header, Locate(snapshot.Header));
            lock (_trackerGate)
                return AdoptLocked(new JsonlSessionHandle(this, id, snapshot.Header, SessionAccess.Read,
                    new StorageHandleState { Cursor = 0, Materialized = true, InheritedEventCount = snapshot.InheritedEventCount }));
        }
        ClaimWrite(id);
        try
        {
            // 大日志且索引可信时直接按索引开写: 打开一份 1M 事件的会话不必先把它整份解压一遍。
            if (TryOpenAppend(id) is { } windowed)
            {
                var appendLease = AcquireLease(id, windowed.Header.Cwd);
                lock (_trackerGate)
                {
                    _leases[id] = appendLease;
                    return AdoptLocked(new JsonlSessionHandle(this, id, windowed.Header, SessionAccess.Write, windowed.State));
                }
            }
            var stored = RequireStoredLog(id);
            var logPath = JsonlLayout.LogPath(_root, stored.Meta.Cwd, id, _compression);
            if (stored.Damage.Any)
            {
                RepairDamagedLog(logPath, stored);
                stored = stored with { Revision = string.Empty, TornTruncateTo = null, RecoveredTail = [] };
            }
            var lease = AcquireLease(id, stored.Meta.Cwd);
            lock (_trackerGate)
            {
                _leases[id] = lease;
                var storedLength = new FileInfo(logPath).Length;
                return AdoptLocked(new JsonlSessionHandle(this, id, stored.Meta, SessionAccess.Write,
                    new StorageHandleState
                    {
                        Cursor = stored.Events.Count,
                        Materialized = true,
                        TornTruncateTo = stored.TornTruncateTo,
                        RecoveredTail = stored.RecoveredTail,
                        InheritedEventCount = stored.InheritedEventCount,
                        Primed = stored.Events,
                        WrittenLength = storedLength,
                    }));
            }
        }
        catch
        {
            ReleaseClaim(id);
            throw;
        }
    }

    public void Flush()
    {
        List<JsonlSessionHandle?> writers;
        lock (_trackerGate) writers = [.. _writers.Values];
        var errors = new List<Exception>();
        foreach (var writer in writers)
        {
            if (writer is null) continue;
            try { writer.Flush(); }
            catch (SessionHandleClosedException) { }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count > 0) throw new AggregateException($"{BackendName} flush failed", errors);
    }

    public SessionPersistenceSnapshot? Stat(SessionId id)
    {
        EnsureRootEncoding();
        lock (_trackerGate)
        {
            // 未落盘的内存会话事件数为 0; 已在磁盘上的日志这里不数事件数(null = 未知)。
            if (_pending.TryGetValue(id, out var pending))
                return new SessionPersistenceSnapshot { Header = pending.Header, Revision = pending.Revision, EventCount = 0, InheritedEventCount = pending.InheritedEventCount };
        }
        var path = FindLog(id);
        return path is null ? null : ReadLogSnapshot(path, id);
    }

    public IReadOnlyList<SessionPersistenceSnapshot> List()
    {
        List<KeyValuePair<SessionId, PendingSession>> pending;
        lock (_trackerGate) pending = [.. _pending];
        var snapshots = ListStoredSnapshots();
        var listed = snapshots.Select(snapshot => snapshot.Header.Id).ToHashSet();
        foreach (var (id, entry) in pending)
            if (!listed.Contains(id))
                snapshots.Add(new SessionPersistenceSnapshot { Header = entry.Header, Revision = entry.Revision, EventCount = 0, InheritedEventCount = entry.InheritedEventCount });
        return snapshots;
    }

    public void Rename(SessionId id, string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        EnsureRootEncoding();
        var snapshot = Stat(id) ?? throw new SessionPersistenceNotFoundException(id);
        var renamed = snapshot.Header with { Title = title };
        var path = FindLog(id);
        if (path is not null)
        {
            // 写句柄的定时落盘会并发追加, 重写头部期间必须持有它的锁。
            JsonlSessionHandle? writer;
            lock (_trackerGate) writer = _writers.GetValueOrDefault(id);
            if (writer is null)
                RewriteHeader(path, renamed, snapshot.InheritedEventCount);
            else
                writer.WhileDrained(expectedLength => RewriteHeader(path, renamed, snapshot.InheritedEventCount, expectedLength));
        }
        List<JsonlSessionHandle> handles;
        lock (_trackerGate)
        {
            if (_pending.TryGetValue(id, out var pending))
                _pending[id] = pending with { Header = renamed };
            handles = [.. _openHandles.Where(handle => handle.Id == id)];
        }
        // 句柄锁在持有期间可能要进 _trackerGate(首次落盘), 这里在 _trackerGate 之外更新, 保持同一加锁顺序。
        foreach (var handle in handles)
            handle.UpdateHeader(renamed);
    }

    public void Delete(SessionId id)
    {
        EnsureRootEncoding();
        var path = FindLog(id);
        var sessionDir = path is null ? null : Path.GetDirectoryName(path);
        List<JsonlSessionHandle> handles;
        lock (_trackerGate)
            handles = [.. _openHandles.Where(handle => handle.Id == id)];
        foreach (var handle in handles)
            handle.Close();
        lock (_trackerGate)
        {
            _pending.Remove(id);
            _writers.Remove(id);
        }
        if (sessionDir is not null && Directory.Exists(sessionDir))
        {
            var trash = Path.Combine(_root, ".trash");
            Directory.CreateDirectory(trash);
            var trashPath = Path.Combine(trash, $"{JsonlLayout.EncodeSegment(id.Value)}-{Guid.NewGuid():N}");
            Directory.Move(sessionDir, trashPath);
            Directory.Delete(trashPath, recursive: true);
        }
    }

    public void Dispose()
    {
        List<JsonlSessionHandle> handles;
        lock (_trackerGate) handles = [.. _openHandles];
        var errors = new List<Exception>();
        foreach (var handle in handles)
        {
            try { handle.Close(); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count > 0) throw new AggregateException($"{BackendName} dispose failed", errors);
    }

    private long RewriteHeader(string path, SessionHeader header, long inheritedEventCount, long? expectedLength = null)
    {
        var original = ReadLiveLog(path);
        if (expectedLength is { } expected && original.LongLength != expected)
            throw new SessionPersistenceCorruptionException(
                $"session \"{header.Id}\": the log changed from {expected} to {original.LongLength} bytes while this writer held it; refusing to rewrite a stale session");
        var headerLine = Encoding.UTF8.GetBytes(
            SessionLogHeader.WriteHeaderLine(header, header.IsSeeded ? inheritedEventCount : null) + "\n");
        byte[] replacement;
        if (_compression == JsonlCompression.None)
        {
            var newline = Array.IndexOf(original, (byte)'\n');
            if (newline < 0)
                throw new SessionPersistenceCorruptionException($"session \"{header.Id}\": log has no header line: {path}");
            replacement = [.. headerLine, .. original.AsSpan(newline + 1).ToArray()];
        }
        else
        {
            var frames = ZstdFrames.Scan(original).Frames;
            if (frames.Count == 0)
                throw new SessionPersistenceCorruptionException($"session \"{header.Id}\": log has no header frame: {path}");
            replacement = [.. ZstdFrames.CompressFrame(headerLine), .. original.AsSpan(frames[0].End).ToArray()];
        }
        var temp = $"{path}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}.tmp";
        File.WriteAllBytes(temp, replacement);
        File.Move(temp, path, overwrite: true);
        return replacement.LongLength;
    }

    internal string? ResolveLog(SessionId id)
    {
        EnsureRootEncoding();
        return FindLog(id);
    }

    /**
     * 窗口化打开: 只把尾部一段读进内存, 更早的事件留给按页重读。
     *
     * 返回 null 只在无法分页时发生: 没有可用的帧视图、索引损坏、尾部读不出来，调用方退回全量打开(能读出来就好), 不做半吊子的降级。
     * 尾窗需要往前让位时, 只让到被替换的那个节点为止。
     */
    public SessionWindow? TryOpenWindow(SessionId id, int tailEvents)
    {
        EnsureRootEncoding();
        if (_compression != JsonlCompression.Zstd || tailEvents <= 0)
            return null;
        var path = FindLog(id);
        if (path is null)
        {
            _warn?.Invoke($"session \"{id}\": no stored log was found for a windowed open");
            return null;
        }
        var reader = WindowReader(path);
        if (reader is null || !reader.TryExtend())
        {
            _warn?.Invoke($"session \"{id}\": no reusable frame view over the log; opening it in full");
            return null;
        }
        long total;
        try
        {
            total = reader.TotalEvents;
        }
        catch (Exception error) when (error is FormatException or SessionPersistenceException)
        {
            _warn?.Invoke($"session \"{id}\": frame view of the log is unusable ({error.Message}); opening it in full");
            return null;
        }
        if (total <= tailEvents)
            return null;
        (long From, IReadOnlyList<SessionEvent> Events) window;
        try
        {
            // 索引里有 replace 信息时一次算准起点, 只解压一遍; 否则先取尾窗, 再由下面的循环按需往前扩。
            var located = reader.ResolveTailStart(tailEvents) is { } resolved
                ? reader.Read(resolved, (int)Math.Min(Math.Max(1, total - resolved), int.MaxValue))
                : reader.ReadTail(tailEvents);
            if (located is not { } tail)
            {
                _warn?.Invoke($"session \"{id}\": the tail of the log could not be located; opening it in full");
                return null;
            }
            window = tail;
        }
        catch (Exception error) when (error is FormatException or SessionPersistenceException)
        {
            _warn?.Invoke($"session \"{id}\": tail window could not be read ({error.Message}); opening it in full");
            return null;
        }

        // 跨边界的 replace **不再拉宽窗口**: 折叠把"本地要收掉的那一段"取成表头到终点,
// 起点在窗口之前的那部分本来就不在 surface 上, 其来源由事件自带的 sourceEventSeqs 表达。
        if (!window.Events.Any(sessionEvent => sessionEvent.Data is RequestHeaderPayload))
        {
            // 请求头是会话级折叠状态: 尾窗里没有就只能按 agent 的 Options 走, 说清楚而不是悄悄退化。
            _warn?.Invoke($"session \"{id}\": the resident window from {window.From} holds no request header; resuming without the persisted request configuration");
        }
        return new SessionWindow(window.From, total, window.Events,
            (offset, count) => ReadWindowPage(reader, offset, count));
    }

    /** 页读失败返回空列表(Session 把它记成缺页, 取值得到 null, 而不是伪造事件), 同时把原因报到日志里。 */
    private IReadOnlyList<SessionEvent> ReadWindowPage(SessionLogWindowReader reader, long offset, int count)
    {
        try
        {
            return reader.Read(offset, count)?.Events ?? [];
        }
        catch (Exception error) when (error is IOException or FormatException)
        {
            _warn?.Invoke($"session log page [{offset}, {offset + count}) could not be read back: {error.Message}");
            return [];
        }
    }

    /**
     * 尾窗里指向窗口之前的 replace 的最早目标 seq: 只覆盖真正需要靠加宽解决的那种。
     * 更早的 replace 不在窗口里, 折叠根本不会重放它, 也就不影响窗口内的 surface。
     */
    private static long? CrossBoundaryReplaceStart(IReadOnlyList<SessionEvent> events, long residentFrom)
    {
        long? earliest = null;
        foreach (var sessionEvent in events)
        {
            if (sessionEvent.SurfaceOp is not SurfaceOp.Replace replace || replace.Start >= residentFrom)
                continue;
            if (earliest is null || replace.Start < earliest)
                earliest = replace.Start;
        }
        return earliest;
    }

    private SessionLogWindowReader? WindowReader(string path)
    {
        lock (_windowGate)
        {
            if (_windowReaders.TryGetValue(path, out var cached))
                return cached;
        }
        var reader = SessionLogWindowReader.TryOpen(path);
        if (reader is null)
            return null;
        lock (_windowGate)
            _windowReaders[path] = reader;
        return reader;
    }

    private void DropWindowReader(string path)
    {
        lock (_windowGate)
            _windowReaders.Remove(path);
    }

    /** 只有足够大的日志才值得走"按索引开写"这条路。 */
    private const long WindowedAppendThreshold = 50_000;

    /**
     * 按索引开写: 游标与已写长度都来自帧索引, 不解压日志。
     *
     * 代价是"打开即修复损伤"不再发生: 索引结构有效(帧连续、末帧到文件末尾)时日志本身可追加,
     * 中段损伤会在读的时候被发现并照旧修。小日志继续走全量解析那条路, 损伤修复语义不变。
     */
    private (SessionHeader Header, StorageHandleState State)? TryOpenAppend(SessionId id)
    {
        if (_compression != JsonlCompression.Zstd)
            return null;
        var logPath = FindLog(id);
        if (logPath is null)
            return null;
        if (SessionLogIndex.TryLoad(logPath) is not { Frames.Count: > 1 } index
            || index.EventCount < WindowedAppendThreshold)
            return null;
        var snapshot = ReadLogSnapshot(logPath, id);
        if (snapshot is null)
            return null;
        return (snapshot.Header, new StorageHandleState
        {
            Cursor = index.EventCount,
            Materialized = true,
            InheritedEventCount = snapshot.InheritedEventCount,
            WrittenLength = index.Length,
        });
    }

    internal bool HasPendingSession(SessionId id)
    {
        lock (_trackerGate) return _pending.ContainsKey(id);
    }

    internal StoredLog ReadStoredLog(string path, SessionId expectedId)
    {
        // 先只看文件元数据: 没变就不必把整份日志读进内存。
        var probe = new FileInfo(path);
        probe.Refresh();
        var probeRevision = FileRevision(probe);
        lock (_parsedGate)
        {
            if (_parsed.TryGetValue(path, out var cached) && cached.Revision == probeRevision)
            {
                TouchLocked(path);
                return cached.Log;
            }
        }
        var (buffer, revision) = ReadStableFile(path);
        if (revision != probeRevision)
        {
            lock (_parsedGate)
            {
                if (_parsed.TryGetValue(path, out var reread) && reread.Revision == revision)
                {
                    TouchLocked(path);
                    return reread.Log;
                }
            }
        }
        var log = ParseStoredLog(buffer, path, expectedId, revision, NeedsFrameIndex(path));
        lock (_parsedGate)
            StoreLocked(path, revision, log);
        return log;
    }

    /** 索引缺失、或者与文件结构对不上(截断/重写/末尾半帧)时才算需要重建。 */
    private bool NeedsFrameIndex(string path)
        => _compression == JsonlCompression.Zstd && SessionLogIndex.TryLoad(path) is null;

    /**
     * 只解压覆盖 [offset, offset + count) 的帧: 索引可信时读一窗就是几十毫秒, 而不是全量展开。
     * 索引缺失或与文件结构对不上(被截断、重写、末尾半帧)返回 null, 调用方退回全量解析并重建索引。
     */
    internal StoredLog? TryReadWindow(string path, SessionId expectedId, long offset, int count)
    {
        if (_compression != JsonlCompression.Zstd || count <= 0 || offset < 0)
            return null;
        var index = SessionLogIndex.TryLoad(path);
        if (index is null)
            return null;
        var (from, to) = index.FramesCovering(offset, count);
        var wanted = new List<int> { 0 };
        for (var frame = from; frame >= 0 && frame <= to; frame += 1)
            if (frame != 0 && index.Frames[frame].EventCount > 0)
                wanted.Add(frame);
        if (wanted.Count == 1 && from >= 0)
            return null;
        var ranges = wanted.Select(frame => index.Frames[frame]).ToList();
        byte[] assembled;
        var frameOffsets = new int[ranges.Count];
        try
        {
            assembled = ReadFrameRanges(path, ranges, frameOffsets, out var total);
            _ = total;
        }
        catch (IOException)
        {
            return null;
        }
        var header = ZstdFrames.DecompressFrame(assembled, frameOffsets[0], (int)(ranges[0].End - ranges[0].Start), (int)ranges[0].Start);
        if (header.Length == 0 || Array.IndexOf(header, (byte)'\n') != header.Length - 1)
            return null;
        var seqBase = ranges.Count > 1 ? ranges[1].FirstSeq : offset;
        var scanner = new SessionLogScanner(header, seqBase);
        var damage = LogDamage.None;
        for (var slot = 1; slot < ranges.Count; slot += 1)
        {
            var length = (int)(ranges[slot].End - ranges[slot].Start);
            try
            {
                scanner.Write(ZstdFrames.DecompressFrame(assembled, frameOffsets[slot], length, (int)ranges[slot].Start));
            }
            catch (FormatException)
            {
                // 窗口之外的坏帧在这里被跳过, 而不是让整次读取失败。
                damage = damage.Plus(1, ranges[slot].Start);
            }
        }
        var parsed = scanner.Finish();
        var windowed = parsed.Events.Where(sessionEvent => sessionEvent.Seq >= offset && sessionEvent.Seq < offset + count).ToList();
        var info = new FileInfo(path);
        info.Refresh();
        return new StoredLog(parsed.Meta, windowed, null, [], parsed.InheritedEventCount, FileRevision(info),
            damage.Plus(parsed.Damage.Records, parsed.Damage.FirstByte));
    }

    /** 只读需要的字节区间, 免得为了取一窗而把 383 MB 全读进内存。 */
    private static byte[] ReadFrameRanges(string path, IReadOnlyList<FrameEntry> ranges, int[] offsets, out int total)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, LiveLogReadShare);
        long sum = 0;
        for (var slot = 0; slot < ranges.Count; slot += 1)
        {
            offsets[slot] = (int)sum;
            sum += ranges[slot].End - ranges[slot].Start;
        }
        if (sum > int.MaxValue)
            throw new IOException($"session log window is larger than addressable memory: {sum} bytes");
        total = 0;
        var buffer = new byte[sum];
        for (var slot = 0; slot < ranges.Count; slot += 1)
        {
            var length = (int)(ranges[slot].End - ranges[slot].Start);
            stream.Position = ranges[slot].Start;
            stream.ReadExactly(buffer, offsets[slot], length);
            total += length;
        }
        return buffer;
    }

    private void TouchLocked(string path)
    {
        _parsedOrder.Remove(path);
        _parsedOrder.Add(path);
    }

    private void StoreLocked(string path, string revision, StoredLog log)
    {
        if (_parsed.ContainsKey(path))
            _parsedOrder.Remove(path);
        _parsed[path] = (revision, log);
        _parsedOrder.Add(path);
        while (_parsedOrder.Count > 1 && EstimatedBytesLocked() > MaxCachedParsedBytes)
        {
            var evicted = _parsedOrder[0];
            _parsedOrder.RemoveAt(0);
            _parsed.Remove(evicted);
        }
    }

    private long EstimatedBytesLocked()
        => _parsedOrder.Sum(path => _parsed.TryGetValue(path, out var entry)
            ? entry.Log.Events.Count * EstimatedBytesPerEvent
            : 0);

    private StoredLog ParseStoredLog(byte[] buffer, string path, SessionId expectedId, string revision, bool rebuildIndex)
    {
        ParsedLog parsed;
        try
        {
            parsed = _compression == JsonlCompression.Zstd ? ReadZstdPrefix(buffer, rebuildIndex) : ReadPlainPrefix(buffer);
        }
        catch (SessionFormatUnsupportedException error)
        {
            throw new SessionFormatUnsupportedException($"{error.Message} (raw log: {path})", new SessionLocation(ArtifactKind, path));
        }
        catch (SessionPersistenceCorruptionException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new SessionPersistenceCorruptionException($"session \"{expectedId}\": stored log is corrupt: {error.Message} (raw log: {path})", error);
        }
        AssertStoredIdentity(path, parsed.Meta, expectedId);
        if (parsed.Meta.Id != expectedId)
            throw new InvalidDataException($"stored session identity mismatch: requested \"{expectedId}\", header contains \"{parsed.Meta.Id}\"");
        var location = Locate(parsed.Meta);
        AssertVersion(parsed.Meta, location);
        // 本 harness 不认识的负载**不再丢弃**: 它们解成不透明记录原样保留, 序号也不重排 ——
// 一旦重排, sourceEventSeqs 与 surfaceOp.start 指向的序号就全部错位(实测让尾窗一路退到文件开头)。
// 只有退役的目录内格式(request/header reason="fallback")仍然拒绝。
        var rejected = DropRetiredFormat(parsed.Events);
        if (rejected > 0)
            parsed = parsed with { InheritedEventCount = Math.Min(parsed.InheritedEventCount, parsed.Events.Count) };
        var damage = parsed.Damage.Plus(rejected, null);
        if (damage.Any)
            _warn?.Invoke(
                $"session \"{parsed.Meta.Id}\": {damage.Records} unreadable log record(s) skipped "
                + $"(first at byte {damage.FirstByte?.ToString(CultureInfo.InvariantCulture) ?? "?"}); the session opens with its readable prefix and every original byte stays on disk");
        else if (rebuildIndex)
            WriteRebuiltIndex(path, buffer.LongLength, parsed);
        return new StoredLog(parsed.Meta, parsed.Events, parsed.TornTruncateTo, parsed.RecoveredTail, parsed.InheritedEventCount, revision, damage);
    }

    /** 丢掉已退役的目录内格式(request/header 的 reason="fallback"), 返回丢掉的条数。 */
    private static int DropRetiredFormat(List<SessionEvent> events)
    {
        var dropped = 0;
        for (var index = events.Count - 1; index >= 0; index -= 1)
        {
            if (events[index].Data is not RequestHeaderPayload { Reason: "fallback" })
                continue;
            events.RemoveAt(index);
            dropped += 1;
        }
        return dropped;
    }

    internal void PersistBatch(SessionHeader header, IReadOnlyList<SessionEvent> events, StorageHandleState state)
    {
        EnsureRootEncoding();
        VerifyLease(header.Id);
        if (state.Materialized)
        {
            AppendLines(header, events, state);
            return;
        }
        state.WrittenLength = Materialize(header, state.InheritedEventCount, events);
        MarkMaterialized(header.Id);
    }

    internal void PersistHeader(SessionHeader header, StorageHandleState state)
    {
        EnsureRootEncoding();
        VerifyLease(header.Id);
        state.WrittenLength = Materialize(header, state.InheritedEventCount, []);
        MarkMaterialized(header.Id);
    }

    /** 该会话当前的写租约持有者; 没有日志或还没建租约时返回 null。 */
    public SessionLeaseHolder? InspectLease(SessionId id)
    {
        if (Stat(id) is not { } snapshot)
            return null;
        return SessionLease.Inspect(SessionLease.PathFor(_root, snapshot.Header.Cwd, id));
    }

    /** 强制接管写租约(CLI 的 take-over): 不要求原持有者退出, 但会让它的下一次落盘失败并停写。 */
    public SessionLeaseHolder ForceTakeOverLease(SessionId id)
    {
        var snapshot = Stat(id) ?? throw new SessionPersistenceNotFoundException(id);
        return SessionLease.ForceTakeOver(SessionLease.PathFor(_root, snapshot.Header.Cwd, id));
    }

    /** 落盘前的 fencing: 租约已被接管就立刻停写。 */
    private void VerifyLease(SessionId id)
    {
        SessionLease? lease;
        lock (_trackerGate) _leases.TryGetValue(id, out lease);
        lease?.Verify();
    }

    private SessionLease AcquireLease(SessionId id, string? cwd)
        => SessionLease.Acquire(SessionLease.PathFor(_root, cwd, id));

    internal void TruncateTornTail(SessionHeader header, long truncateTo)
    {
        var path = JsonlLayout.LogPath(_root, header.Cwd, header.Id, _compression);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.SetLength(truncateTo);
        stream.Flush(true);
    }

    /** 就地重写日志到前 eventCount 个事件(D3 revert): 头行 + 保留事件整体原子替换, 失败不改原文件。 */
    internal long? TruncateLog(SessionHeader header, long eventCount)
    {
        var path = FindLog(header.Id);
        if (path is null)
            return null;
        var stored = ReadStoredLog(path, header.Id);
        var keep = (int)Math.Clamp(eventCount, 0, stored.Events.Count);
        if (keep == stored.Events.Count)
            return null;
        var kept = stored.Events.GetRange(0, keep);
        var (content, _, _) = EncodeWholeLog(stored.Meta, stored.InheritedEventCount, kept);
        var temp = $"{path}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}.tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(true);
        }
        File.Move(temp, path, overwrite: true);
        SessionLogIndex.Delete(path);
        DropWindowReader(path);
        return content.Length;
    }

    internal void ReleaseHandle(JsonlSessionHandle handle, bool materialized)
    {
        SessionLease? lease;
        lock (_trackerGate)
        {
            _openHandles.Remove(handle);
            if (handle.Access != SessionAccess.Write) return;
            _writers.Remove(handle.Id);
            if (!materialized) _pending.Remove(handle.Id);
            _leases.Remove(handle.Id, out lease);
        }
        // 让出租约要在 _trackerGate 之外: 释放要等另一个进程的短锁, 不能卡住所有会话。
        lease?.Dispose();
    }

    private StoredLog RequireStoredLog(SessionId id)
        => FindLog(id) is { } path
            ? ReadStoredLog(path, id)
            : throw new SessionPersistenceNotFoundException(id);

    private ParsedLog ReadPlainPrefix(byte[] buffer)
    {
        var scan = SessionLogScannerCompat.ScanLog(buffer);
        // 有损伤时不用"最后一条好记录"去截尾: 那会把损伤之后的字节全删掉。截尾只用于末尾半行。
        long? tornTail = scan.Damage.Any
            ? null
            : scan.CommittedBytes < buffer.Length ? scan.CommittedBytes : null;
        return new ParsedLog(scan.Meta, scan.Events, tornTail, [], scan.InheritedEventCount, scan.Damage);
    }

    private ParsedLog ReadZstdPrefix(byte[] buffer, bool collectFrames)
    {
        var scan = ZstdFrames.Scan(buffer);
        if (scan.Frames.Count == 0) throw new FormatException("empty or header-less Zstandard session log");
        var headerPlaintext = DecompressFrame(buffer, scan.Frames[0]);
        AssertZstdHeaderFrame(headerPlaintext);
        var scanner = new SessionLogScanner(headerPlaintext);
        // 每帧解压后的字节数: 解压前就算出来(只是读帧头, 1M 帧几十毫秒), 解压才能一次开够目标数组。
        var sizes = ZstdFrames.PlaintextSizes(buffer, scan.Frames);
        // 拼接帧一次解整段最快; 单帧坏掉时退回逐帧, 把可读的帧全取出来。
        var (decoded, failedFrames) = ZstdFrames.DecompressFramesResilient(scan.Frames, buffer, skip: 1, sizes);
        // 逐帧喂: 帧头写着每帧解压后的字节数, 于是这一整段解压结果能按帧切开, 顺手记下每帧出多少条事件。
        // 重建帧索引要的就是这些边界, 这样"首次全量打开"只多付切分, 不再多付一次解压; 索引已经是新的就不切。
        var frames = FeedFrames(scanner, buffer, decoded, scan.Frames, collectFrames, sizes);
        var frameDamage = scan.Damaged.Count + failedFrames.Count;
        if (scan.TornStart is not { } tornStart)
        {
            var complete = scanner.Finish();
            var reported = complete.Damage.Plus(frameDamage, scan.Damaged.Count > 0 ? scan.Damaged[0].Start : null);
            return new ParsedLog(complete.Meta, complete.Events, null, [], complete.InheritedEventCount, reported, frames);
        }
        // 尾帧里可能还藏着完整记录: 收下它们。只有损伤确实发生在完整帧里时才保留字节并修复,
        // 否则这就是一次普通的"半帧崩溃", 截掉那半帧即可。
        var damageBeforeTail = scanner.Damage.Records;
        var eventsBeforeTail = scanner.Checkpoint().EventCount;
        scanner.Write(ZstdFrames.DecompressPrefix(buffer[tornStart..]));
        var finished = scanner.Finish();
        var realDamage = frameDamage > 0 || damageBeforeTail > 0;
        return new ParsedLog(
            finished.Meta,
            finished.Events,
            realDamage ? null : tornStart,
            [.. finished.Events.Skip((int)eventsBeforeTail)],
            finished.InheritedEventCount,
            realDamage ? finished.Damage.Plus(frameDamage, scan.Damaged.Count > 0 ? scan.Damaged[0].Start : null) : LogDamage.None);
    }

    private static byte[] DecompressFrame(byte[] buffer, ZstdFrames.FrameRange frame)
        => ZstdFrames.DecompressFrame(buffer, frame.Start, frame.End - frame.Start, frame.Start);

    /**
     * 逐帧喂解压结果, 顺手记下每帧解出的事件条数与首条 seq: 重建帧索引要的就是这两样。
     * 帧头声明了每帧解压后的字节数, 所以"一次解压出来的连续明文"能按帧切开, 不必再解压一遍。
     *
     * 索引已经是最新时(collect 为 false)整段喂, 不为一份不用重建的索引多付切分。
     * 有一帧没写长度、或者长度与解压结果对不上时同样整段喂并返回 null: 索引可以不建, 事件必须读全。
     */
    private static List<FrameEntry>? FeedFrames(
        SessionLogScanner scanner, byte[] compressed, byte[] decoded, IReadOnlyList<ZstdFrames.FrameRange> ranges,
        bool collect, long[]? sizes)
    {
        if (!collect)
        {
            scanner.Write(decoded);
            return null;
        }
        if (!TilesFile(compressed.Length, ranges) || sizes is not { } plaintextSizes)
        {
            scanner.Write(decoded);
            return null;
        }
        long plaintext = 0;
        for (var slot = 1; slot < plaintextSizes.Length; slot += 1)
            plaintext += plaintextSizes[slot];
        if (plaintext != decoded.Length)
        {
            scanner.Write(decoded);
            return null;
        }
        var entries = new List<FrameEntry>(ranges.Count) { new(0, ranges[0].End, 0, 0) };
        var at = 0;
        long seq = 0;
        for (var slot = 1; slot < ranges.Count; slot += 1)
        {
            var length = (int)plaintextSizes[slot];
            scanner.Write(decoded.AsSpan(at, length));
            var count = (int)(scanner.Checkpoint().EventCount - seq);
            entries.Add(new FrameEntry(ranges[slot].Start, ranges[slot].End, seq, count));
            at += length;
            seq += count;
        }
        return entries;
    }

    /** 帧必须从字节 0 起首尾相接地铺满整份日志: 有缺口或半帧时切出来的边界不能当索引。 */
    private static bool TilesFile(long length, IReadOnlyList<ZstdFrames.FrameRange> ranges)
        => ranges.Count > 0 && ranges[0].Start == 0 && ranges[^1].End == length;

    /**
     * 全量读成功之后补写一次帧索引: 只有这样"退回全量打开"才不会变成永久退回 ——
     * 下一次打开就能走窗口化(只切帧 + 解压尾窗), 而不是再解压整份日志。
     *
     * 只在没有任何损伤时写: 坏的帧边界一旦落盘, 之后每次读都会按错的边界去切, 比没有索引更糟。
     * 已经有一份能通过结构校验的索引就不重写(它拿末帧对齐文件长度校验, 对得上就是最新的)。
     */
    private void WriteRebuiltIndex(string path, long length, ParsedLog parsed)
    {
        if (_compression != JsonlCompression.Zstd || parsed.Frames is not { Count: > 1 } frames)
            return;
        if (frames[^1].End != length || SessionLogIndex.TryLoad(path) is not null)
            return;
        var index = new SessionLogIndex
        {
            Length = length,
            EventCount = frames[^1].FirstSeq + frames[^1].EventCount,
            SeqBase = frames[0].FirstSeq,
            Frames = frames,
        };
        try
        {
            index.Save(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke($"session \"{parsed.Meta.Id}\": the frame index could not be written ({error.Message}); the next open reads the log in full again");
            return;
        }
        // 窗口读者可能已经按"没有索引"切过帧: 让它下一次打开重新用索引(里面的帧与 seq 区间才算数)。
        DropWindowReader(path);
    }

    private static void AssertZstdHeaderFrame(byte[] plaintext)
    {
        if (plaintext.Length == 0 || Array.IndexOf(plaintext, (byte)'\n') != plaintext.Length - 1)
            throw new FormatException("corrupt Zstandard session log: first frame is not exactly one header line");
    }

    private long Materialize(SessionHeader meta, long inheritedEventCount, IReadOnlyList<SessionEvent> events)
    {
        var dir = JsonlLayout.SessionDir(_root, meta.Cwd, meta.Id);
        var finalPath = JsonlLayout.LogPath(_root, meta.Cwd, meta.Id, _compression);
        RejectOppositeArtifact(meta.Cwd, meta.Id);
        var (content, headerFrameLength, frames) = EncodeWholeLog(meta, inheritedEventCount, events);

        Directory.CreateDirectory(dir);
        if (File.Exists(finalPath))
            throw new InvalidOperationException($"refusing to materialize \"{meta.Id}\": a log already exists on disk (open it instead)");
        var temp = $"{finalPath}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}.tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(true);
        }
        try
        {
            File.Move(temp, finalPath);
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            if (File.Exists(finalPath)) throw new SessionAlreadyExistsException(meta.Id);
            throw;
        }
        WriteIndex(finalPath, inheritedEventCount, events.Count, headerFrameLength, frames);
        return content.Length;
    }

    /**
     * 整份日志的字节: 头帧 + 按预算切开的事件帧。物化、截断重写、损伤修复都走这里,
     * 于是"一帧多大"只有一处定义 —— 事件帧与追加路径用同一套切帧规则。
     */
    private (byte[] Content, int HeaderFrameLength, List<(byte[] Content, long FirstSeq, int EventCount, long MinReplaceStart)> Frames)
        EncodeWholeLog(SessionHeader meta, long inheritedEventCount, IReadOnlyList<SessionEvent> events)
    {
        var headerLine = SessionLogHeader.WriteHeaderLine(meta, meta.IsSeeded ? inheritedEventCount : null) + "\n";
        var headerBytes = Encoding.UTF8.GetBytes(headerLine);
        var headerFrame = _compression == JsonlCompression.Zstd
            ? ZstdFrames.CompressFrame(headerBytes)
            : headerBytes;
        // 物化也不能把整批事件塞进一帧: 那会让一帧明文达到预算的好几倍,
        // 窗口化打开要整帧解压, 字节预算等于没设。
        var frames = EncodeEventFrames(events);
        var content = new byte[headerFrame.Length + frames.Sum(frame => frame.Content.Length)];
        headerFrame.CopyTo(content, 0);
        var offset = headerFrame.Length;
        foreach (var frame in frames)
        {
            frame.Content.CopyTo(content, offset);
            offset += frame.Content.Length;
        }
        return (content, headerFrame.Length, frames);
    }

    /** 物化写完后按实际帧数写索引: 头帧一条, 之后每个事件帧一条(边界与计数都来自切帧本身)。 */
    private void WriteIndex(string finalPath, long inheritedEventCount, int eventCount, int headerFrameLength,
        List<(byte[] Content, long FirstSeq, int EventCount, long MinReplaceStart)> frames)
    {
        if (_compression != JsonlCompression.Zstd)
            return;
        var entries = new List<FrameEntry> { new(0, headerFrameLength, 0, 0) };
        var offset = headerFrameLength;
        foreach (var frame in frames)
        {
            entries.Add(new FrameEntry(offset, offset + frame.Content.Length, frame.FirstSeq, frame.EventCount)
            {
                MinReplaceStart = frame.MinReplaceStart,
            });
            offset += frame.Content.Length;
        }
        var seqBase = eventCount > 0 ? frames[0].FirstSeq : inheritedEventCount;
        SessionLogIndex.Append(finalPath, entries, eventCount, seqBase);
    }

    /**
     * 把一批事件编成若干帧: 逐条编码成存储行, 按行的**真实字节**攒到预算就收一帧。
     *
     * 预算按编码后的字节结算, 不预判"这条事件大概多大": 一行多大取决于它最终被编成什么
     * (普通记录、还是打包行里的一个成员), 事前无从知道; 编完再量既精确又不必把最贵的一步做两次。
     * 帧边界只落在行边界上 —— 一行是一条完整记录, 劈开就读不回来。
     */
    private List<(byte[] Content, long FirstSeq, int EventCount, long MinReplaceStart)> EncodeEventFrames(IReadOnlyList<SessionEvent> events)
    {
        var records = ChunkRows.EncodeRecords(events, _packChunks);
        var frames = new List<(byte[] Content, long FirstSeq, int EventCount, long MinReplaceStart)>();
        var frame = new ArrayBufferWriter<byte>();
        long firstSeq = 0;
        var eventCount = 0;
        var minReplaceStart = long.MaxValue;
        foreach (var record in records)
        {
            // 每条记录只编码一次: 编码后的字节数就是它的真实体积, 预算按它结算, 不再另算一遍。
            var line = Encoding.UTF8.GetBytes(record.Line);
            var recordBytes = line.Length + 1;
            // 一个空帧里也要能装下这一行: 单行超过预算时它独占一帧, 否则永远塞不进去。
            if (eventCount > 0 && frame.WrittenCount + recordBytes > WriteBatch.MaxPlaintextBytes)
            {
                frames.Add(Materialize(frame, firstSeq, eventCount, minReplaceStart));
                frame = new ArrayBufferWriter<byte>();
                eventCount = 0;
                minReplaceStart = long.MaxValue;
            }

            if (eventCount == 0)
                firstSeq = record.FirstSeq;
            minReplaceStart = Math.Min(minReplaceStart, record.MinReplaceStart);
            line.CopyTo(frame.GetSpan(line.Length));
            frame.Advance(line.Length);
            frame.GetSpan(1)[0] = (byte)'\n';
            frame.Advance(1);
            eventCount += record.EventCount;
        }

        if (eventCount > 0)
            frames.Add(Materialize(frame, firstSeq, eventCount, minReplaceStart));
        return frames;

        (byte[] Content, long FirstSeq, int EventCount, long MinReplaceStart) Materialize(
            ArrayBufferWriter<byte> text, long seq, int count, long replaceStart)
            => (_compression == JsonlCompression.Zstd
                ? ZstdFrames.CompressFrame(text.WrittenSpan)
                : text.WrittenSpan.ToArray(), seq, count, replaceStart);
    }

    private void AppendLines(SessionHeader meta, IReadOnlyList<SessionEvent> events, StorageHandleState state)
    {
        var frames = EncodeEventFrames(events);
        var path = JsonlLayout.LogPath(_root, meta.Cwd, meta.Id, _compression);
        long before;
        using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, LiveLogReadShare))
        {
            before = probe.Length;
        }
        // 日志在本写入者两次落盘之间被别人推进过: 本写入者的序号已经落后于文件内容。
        // 这时继续追加会把两套序号交织进同一个文件, 之后整份日志都读不回来。
        if (state.WrittenLength is { } expected && before != expected)
            throw new SessionPersistenceCorruptionException(
                $"session \"{meta.Id}\": the log grew from {expected} to {before} bytes while this writer held it — another process appended to the same session; refusing to interleave events");
        var entries = new List<FrameEntry>(frames.Count);
        long total = 0;
        foreach (var frame in frames)
        {
            entries.Add(new FrameEntry(before + total, before + total + frame.Content.Length, frame.FirstSeq, frame.EventCount)
            {
                MinReplaceStart = frame.MinReplaceStart,
            });
            total += frame.Content.Length;
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            foreach (var frame in frames)
                stream.Write(frame.Content);
            stream.Flush(true);
        }
        catch
        {
            state.WrittenLength = before;
            RollbackAppend(path, before);
            throw;
        }
        state.WrittenLength = before + total;
        if (_compression == JsonlCompression.Zstd && entries.Count > 0)
            SessionLogIndex.Append(path, entries, events.Count, events[0].Seq);
    }

    private static void RollbackAppend(string path, long size)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.SetLength(size);
        stream.Flush(true);
    }

    /**
     * 读一份快照, 并确认读取期间文件没被追加。
     * 正常情况下只读一次; 只有在读的当口写入方又追加了才重读一遍, 第二次仍不稳定就按已知长度截断返回。
     */
    private (byte[] Buffer, string Revision) ReadStableFile(string path)
    {
        var info = new FileInfo(path);
        for (var attempt = 0; ; attempt += 1)
        {
            info.Refresh();
            var before = FileRevision(info);
            var buffer = ReadLiveLog(path);
            var afterInfo = new FileInfo(path);
            afterInfo.Refresh();
            if (before == FileRevision(afterInfo)) return (buffer, before);
            if (attempt == 1) return (buffer[..(int)Math.Min(buffer.Length, info.Length)], before);
            info = afterInfo;
        }
    }

    /**
     * 一次分配读完整个日志。
     * 旧实现走 MemoryStream + CopyTo + ToArray: 内容先在 MemoryStream 的内部缓冲里存一份,
     * ToArray 再复制一份, 34 MB 的日志因此峰值占两份内存。按已知长度直接分配目标数组后 ReadExactly,
     * 边读边把实际读到的长度缩减, 就只需要一块缓冲。
     */
    private static byte[] ReadLiveLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, LiveLogReadShare);
        var length = stream.Length;
        if (length == 0)
            return [];
        if (length > int.MaxValue)
            throw new FormatException($"session log is larger than addressable memory: {length} bytes");
        var buffer = new byte[length];
        var read = 0;
        while (read < buffer.Length)
        {
            // 读取期间写入方可能追加, Length 是那时快照; 读不满就按实际读到的截断。
            var chunk = stream.Read(buffer, read, buffer.Length - read);
            if (chunk == 0)
                break;
            read += chunk;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static string FileRevision(FileInfo info)
    {
        info.Refresh();
        return $"file:{info.Length}:{info.LastWriteTimeUtc.Ticks}:{info.CreationTimeUtc.Ticks}";
    }

    private string? ReadFirstLineAny(string path)
        => _compression == JsonlCompression.Zstd ? ReadFirstZstdLine(path) : ReadFirstLine(path);

    private static string? ReadFirstLine(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, LiveLogReadShare);
        using var accumulated = new MemoryStream();
        var buffer = new byte[FirstLineChunkSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (newline >= 0)
            {
                accumulated.Write(buffer, 0, newline);
                return Encoding.UTF8.GetString(accumulated.ToArray());
            }
            accumulated.Write(buffer, 0, read);
        }
        return null;
    }

    private static string? ReadFirstZstdLine(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, LiveLogReadShare);
        using var accumulated = new MemoryStream();
        var chunk = new byte[FirstLineChunkSize];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            accumulated.Write(chunk, 0, read);
            var content = accumulated.ToArray();
            var frames = ZstdFrames.Scan(content, maxFrames: 1).Frames;
            if (frames.Count == 0) continue;
            byte[] plaintext;
            try
            {
                plaintext = ZstdFrames.DecompressFrame(content, frames[0].Start, frames[0].End - frames[0].Start, frames[0].Start);
            }
            catch (FormatException error)
            {
                throw new FormatException("corrupt Zstandard session log: header frame failed validation", error);
            }
            AssertZstdHeaderFrame(plaintext);
            return Encoding.UTF8.GetString(plaintext, 0, plaintext.Length - 1);
        }
        return null;
    }

    private string? FindLog(SessionId id)
    {
        var matches = new List<string>();
        foreach (var project in ListProjectDirs())
        {
            RejectLegacyFlatArtifact(project, id);
            var dir = Path.Join(project, JsonlLayout.EncodeSegment(id.Value));
            var path = Path.Join(dir, $"session{JsonlLayout.LogSuffix(_compression)}");
            var opposite = Path.Join(dir, $"session{JsonlLayout.LogSuffix(OppositeCompression())}");
            if (ExistsFile(opposite)) throw EncodingMismatch(opposite);
            if (ExistsFile(path)) matches.Add(path);
        }
        if (matches.Count > 1)
            throw new InvalidDataException($"duplicate JSONL session id \"{id}\" appears in multiple project directories");
        return matches.Count == 0 ? null : matches[0];
    }

    private List<SessionPersistenceSnapshot> ListStoredSnapshots()
    {
        var snapshots = new List<SessionPersistenceSnapshot>();
        var ids = new HashSet<SessionId>();
        foreach (var project in ListProjectDirs())
        {
            foreach (var dir in ListSessionDirs(project))
            {
                var opposite = Path.Join(dir, $"session{JsonlLayout.LogSuffix(OppositeCompression())}");
                if (ExistsFile(opposite)) throw EncodingMismatch(opposite);
                var path = Path.Join(dir, $"session{JsonlLayout.LogSuffix(_compression)}");
                if (!ExistsFile(path)) continue;
                SessionPersistenceSnapshot? snapshot;
                try
                {
                    snapshot = ReadLogSnapshot(path, null);
                }
                catch (SessionFormatUnsupportedException)
                {
                    continue;
                }
                if (snapshot is null) continue;
                if (!ids.Add(snapshot.Header.Id))
                    throw new InvalidDataException($"duplicate JSONL session id \"{snapshot.Header.Id}\" appears in multiple project directories");
                snapshots.Add(snapshot);
            }
        }
        return snapshots;
    }

    /** Stat and List both build stored snapshots here, reading only the log's header line or frame. */
    private SessionPersistenceSnapshot? ReadLogSnapshot(string path, SessionId? expectedId)
    {
        string? first;
        try
        {
            first = ReadFirstLineAny(path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        if (first is null) return null;
        SessionStorageMetadata? stored;
        try
        {
            stored = SessionLogHeader.ParseHeader(first);
        }
        catch (SessionFormatUnsupportedException error)
        {
            throw new SessionFormatUnsupportedException($"{error.Message} (raw log: {path})", new SessionLocation(ArtifactKind, path));
        }
        if (stored is null) return null;
        AssertStoredIdentity(path, stored.Meta, expectedId);
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists) return null;
        return new SessionPersistenceSnapshot
        {
            Header = stored.Meta,
            Revision = FileRevision(info),
            SizeBytes = info.Length,
            InheritedEventCount = stored.InheritedEventCount,
        };
    }

    private List<string> ListProjectDirs()
        => Directory.Exists(_root) ? [.. Directory.GetDirectories(_root)] : [];

    private static List<string> ListSessionDirs(string project)
    {
        var entries = Directory.GetFileSystemEntries(project);
        foreach (var entry in entries)
        {
            if (File.Exists(entry) && (entry.EndsWith(".jsonl", StringComparison.Ordinal) || entry.EndsWith(".jsonl.zstd", StringComparison.Ordinal)))
                throw LegacyLayout(entry);
        }
        return [.. entries.Where(Directory.Exists)];
    }

    private void CheckRootEncoding()
    {
        foreach (var project in ListProjectDirs())
        {
            foreach (var dir in ListSessionDirs(project))
            {
                var incompatible = Path.Join(dir, $"session{JsonlLayout.LogSuffix(OppositeCompression())}");
                if (ExistsFile(incompatible)) throw EncodingMismatch(incompatible);
            }
        }
    }

    private void EnsureRootEncoding() => _ = _rootEncodingCheck.Value;

    private void RejectLegacyFlatArtifact(string project, SessionId id)
    {
        var encoded = JsonlLayout.EncodeSegment(id.Value);
        foreach (var compression in new[] { JsonlCompression.Zstd, JsonlCompression.None })
        {
            var path = Path.Join(project, encoded + JsonlLayout.LogSuffix(compression));
            if (ExistsFile(path)) throw LegacyLayout(path);
        }
    }

    private void RejectOppositeArtifact(string? cwd, SessionId id)
    {
        var path = JsonlLayout.LogPath(_root, cwd, id, OppositeCompression());
        if (ExistsFile(path)) throw EncodingMismatch(path);
    }

    private JsonlCompression OppositeCompression()
        => _compression == JsonlCompression.Zstd ? JsonlCompression.None : JsonlCompression.Zstd;

    private Exception EncodingMismatch(string path)
        => new InvalidOperationException(
            $"session artifact \"{path}\" uses {JsonlLayout.LogSuffix(OppositeCompression())}, "
            + $"but this backend is configured for compression \"{_compression.ToString().ToLowerInvariant()}\"; "
            + "use a separate root or select the matching compression mode");

    private static Exception LegacyLayout(string path)
        => new InvalidOperationException(
            $"session artifact \"{path}\" uses the unsupported flat-file layout; "
            + "use a separate root or move it into a project/session directory before loading");

    private static bool ExistsFile(string path) => File.Exists(path);

    private SessionLocation Locate(SessionHeader meta)
        => new(ArtifactKind, JsonlLayout.LogPath(_root, meta.Cwd, meta.Id, _compression));

    private void AssertStoredIdentity(string path, SessionHeader meta, SessionId? expectedId)
    {
        if (expectedId is { } expected && meta.Id != expected)
            throw new InvalidDataException($"corrupt session log \"{path}\": requested id \"{expected}\" does not match header id \"{meta.Id}\"");
        var expectedPath = JsonlLayout.LogPath(_root, meta.Cwd, meta.Id, _compression);
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedPath), StringComparison.Ordinal))
            throw new InvalidDataException($"corrupt session log \"{path}\": header id \"{meta.Id}\" and cwd identify \"{expectedPath}\"");
    }

    private static void AssertVersion(SessionHeader meta, SessionLocation? location)
    {
        if (meta.Version != SessionHeader.SessionFormatVersion)
            throw Unsupported(SessionRefusals.FormatVersionRefusal(meta.Id.Value, meta.Version), location);
    }

    private static SessionFormatUnsupportedException Unsupported(string reason, SessionLocation? location)
        => new(location is null ? reason : $"{reason} (raw log: {location.Path})", location);

    /**
     * 损伤日志在首次以写身份打开时的修复: 原文另存一份, 再把规范日志按可读前缀重写。
     *
     * 为什么必须重写而不是直接追加: 事件序列要求 seq 从 0 连续(Session 的构造契约), 而损伤之后的记录已经无法
     * 知道它们原本的 seq —— 只有把可读前缀按 0..n-1 重排, 会话才能继续正常写入。原文留在 `.damaged-时间戳` 里,
     * 一个字节都没丢, 将来 codec 修好还能捞回来。
     */
    private void RepairDamagedLog(string path, StoredLog stored)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var sidecar = $"{path}.damaged-{stamp}";
        for (var attempt = 1; File.Exists(sidecar) && attempt <= 100; attempt += 1)
            sidecar = $"{path}.damaged-{stamp}-{attempt}";
        File.Copy(path, sidecar);
        var (content, _, _) = EncodeWholeLog(stored.Meta, stored.InheritedEventCount, stored.Events);
        var temp = $"{path}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}.tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(true);
        }
        File.Move(temp, path, overwrite: true);
        SessionLogIndex.Delete(path);
        DropWindowReader(path);
        lock (_parsedGate)
        {
            _parsed.Remove(path);
            _parsedOrder.Remove(path);
        }
        _warn?.Invoke(
            $"session \"{stored.Meta.Id}\": {stored.Damage.Records} unreadable log record(s) were skipped to open this session; "
            + $"the live log was rewritten from the {stored.Events.Count} readable event(s) and the original bytes are preserved at {sidecar}");
    }

    private void ClaimWrite(SessionId id)
    {
        lock (_trackerGate)
        {
            if (_writers.ContainsKey(id)) throw new SessionAlreadyOwnedException(id);
            _writers[id] = null;
        }
    }

    private void ReleaseClaim(SessionId id)
    {
        SessionLease? lease;
        lock (_trackerGate)
        {
            _writers.Remove(id);
            _leases.Remove(id, out lease);
        }
        lease?.Dispose();
    }

    private void MarkMaterialized(SessionId id)
    {
        lock (_trackerGate) _pending.Remove(id);
    }

    private JsonlSessionHandle AdoptLocked(JsonlSessionHandle handle)
    {
        _openHandles.Add(handle);
        if (handle.Access == SessionAccess.Write) _writers[handle.Id] = handle;
        return handle;
    }
}

