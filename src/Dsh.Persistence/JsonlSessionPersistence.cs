using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Persistence;

public sealed class JsonlSessionPersistence : ISessionPersistence, IDisposable
{
    private const int FirstLineChunkSize = 8192;

    /** 追加时抢"尾部这段字节"的重试次数与间隔: 抢不到只是这一次写不进去, 不是日志坏了。 */
    private const int TailLockAttempts = 100;
    private static readonly TimeSpan TailLockRetryDelay = TimeSpan.FromMilliseconds(5);
    private const string BackendName = "session-persistence-jsonl";
    /** 表面派生缓存的文件头魔数与版本: 格式一变就让旧缓存作废, 不要拿旧列表去当折叠结果。 */
    private const string SurfaceFoldMagic = "surface-fold";
    private const int SurfaceFoldVersion = 1;
    /** 超过这么多节点的表面就不缓存了: 没压过的百万级会话本来就要折一遍, 5MB 的缓存只会拖慢每次落盘。 */
    private const int MaxCachedSurfaceNodes = 32_768;
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
        lock (_trackerGate)
        {
            if (_pending.ContainsKey(header.Id) || _writers.ContainsKey(header.Id))
                throw new SessionAlreadyExistsException(header.Id);
            _writers[header.Id] = null;
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
                lock (_trackerGate)
                    return AdoptLocked(new JsonlSessionHandle(this, id, windowed.Header, SessionAccess.Write, windowed.State));
            }
            var stored = RequireStoredLog(id);
            var logPath = JsonlLayout.LogPath(_root, stored.Meta.Cwd, id, _compression);
            if (stored.Damage.Any)
            {
                RepairDamagedLog(logPath, stored);
                stored = stored with { Revision = string.Empty, TornTruncateTo = null, RecoveredTail = [] };
            }
            else if (TryReframeToWriteBudget(logPath, stored))
            {
                // 重写只改帧边界: 事件与头行不变, 但文件与缓存的解析结果都换了一份。
                stored = stored with { Revision = string.Empty, TornTruncateTo = null, RecoveredTail = [] };
            }
            lock (_trackerGate)
            {
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

    /**
     * 落盘条数: 先问帧索引(一次 stat + 命中缓存), 没有索引才解析。
     *
     * **必须给真数**: 发号票用它决定起点, 回 0 等于把整份日志当作还没写过。
     * 清单快照在"数不清"时给的是 null(它只做列举), 那不是 0, 要退回解析一条路。
     */
    public long StoredEventCount(SessionId id)
    {
        EnsureRootEncoding();
        var path = FindLog(id);
        if (path is null)
            return 0;
        if (SessionLogIndex.TryLoad(path) is { } index)
            return index.EventCount;
        if (ReadLogSnapshot(path, id)?.EventCount is { } counted)
            return counted;
        return ReadStoredLog(path, id).Events.Count;
    }

    /**
     * 日志末尾有没有"不属于任何事件"的字节(半帧、别人的半截写)。
     *
     * 有的话不能往上追加: 追加会落在那些字节之后, 之后的整条日志都读不回来。
     * 这种情况按原样交给上层处理(重开一次由打开逻辑修损伤), 不在追加路径里猜。
     */
    internal bool IsLogTailClean(SessionId id)
    {
        EnsureRootEncoding();
        var path = FindLog(id);
        if (path is null)
            return false;
        var info = new FileInfo(path);
        if (SessionLogIndex.TryLoad(path) is { } index)
            return index.Length == info.Length;
        var stored = ReadStoredLog(path, id);
        return stored.TornTruncateTo is null && !stored.Damage.Any;
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
        // 头帧长度随标题而变: 整份日志的后续字节因此平移 delta 字节, 帧索引与缓存的窗口读者必须跟着走。
        // 不跟着走的索引会让"末帧到文件尾"照样成立(后续追加用的是新长度), 于是错位的边界被当成权威 ——
        // 窗口读按它解压必然失败, 首页再也读不回来。
        SessionLogIndex.Shift(path, replacement.LongLength - original.LongLength);
        DropWindowReader(path);
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
        // 尾窗里有没有 request/header 不在这里告警: 恢复路径由 AgentLoop.UsesWindowedRestore 把关(缺头就整份恢复),
        // 其余调用方(宿主快照预览)只要条数与事件, 拿不到请求头是正常的 —— 从这条路径喊"少一截"只会误导。
        return new SessionWindow(window.From, total, window.Events,
            (offset, count) => ReadWindowPage(path, offset, count));
    }

    /**
     * 页读失败返回空列表(Session 把它记成缺页, 取值得到 null, 而不是伪造事件), 同时把原因报到日志里。
     *
     * 一页读不出来通常是帧视图与日志内容不符(日志被改写过/别处重建过索引): 先丢掉索引与帧视图、
     * 按日志本身的帧头重切一次再试。"这一页永远读不回来"会把整份会话变成打不开(GUI 因此连主窗口都出不来)。
     */
    private IReadOnlyList<SessionEvent> ReadWindowPage(string path, long offset, int count)
    {
        for (var attempt = 0; attempt < 3; attempt += 1)
        {
            var reader = WindowReader(path);
            if (reader is null)
                return [];
            try
            {
                return reader.Read(offset, count)?.Events ?? [];
            }
            catch (Exception error) when (error is IOException or FormatException)
            {
                _warn?.Invoke($"session log page [{offset}, {offset + count}) could not be read back: {error.Message}");
                if (attempt == 0)
                {
                    // 第一次: 多半是索引与日志对不上, 丢掉索引重切一次。
                    DropStaleIndex(path);
                    continue;
                }
                if (attempt == 1)
                {
                    // 第二次: 盘上那一段本身不自洽(序号回退)。全量解析会拼接回退前后的两段并把日志原子重写,
                    // 之后再读这一页就正常了。
                    DropParsed(path);
                    _ = ReadStoredLog(path, SessionId.Create(Path.GetFileName(Path.GetDirectoryName(path)!)));
                    DropStaleIndex(path);
                    continue;
                }
                return [];
            }
        }
        return [];
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

    /** 索引与日志不再相符(帧边界错位): 索引与帧视图一起丢掉, 下一次全量打开会按日志重建索引。 */
    private void DropStaleIndex(string path)
    {
        SessionLogIndex.Delete(path);
        DropWindowReader(path);
    }

    /**
     * 日志的帧边界是否还是"现在这套写入规则"的: 一帧大于写盘批次的明文字节预算就算不是。
     *
     * 这里不用"超过预算几倍"这种门槛: 写入方本来就用同一个预算切帧, 比它大就说明这份日志是旧规则写的,
     * 而旧规则的大帧正是"翻一页要解压几十 MB"的唯一来源。判据因此是"与写入规则一致", 不是某个经验数值。
     *
     * 重写只在持有写句柄时进行(此时没有别的写入者); 失败就保持原样并留一条告警, 绝不因此丢掉会话。
     */
    private bool TryReframeToWriteBudget(string path, StoredLog stored)
    {
        // 末尾半帧是"写了一半"的现场: 它由截断处理, 重写会把那一帧一起改掉(还会多一条告警)。
        if (_compression != JsonlCompression.Zstd || stored.Events.Count == 0 || stored.Damage.Any
            || stored.TornTruncateTo is not null)
            return false;
        // 索引必须与日志、事件列表三方对上才敢按帧重写; 对不上就不动它(下一次全量打开会重建索引)。
        if (FrameLayout(path, stored) is not { } layout)
            return false;
        var budget = (long)WriteBatch.MaxPlaintextBytes;
        var temp = $"{path}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}.tmp";
        (long Length, List<FrameEntry> Entries)? written;
        try
        {
            written = WriteReframedLog(path, temp, layout, budget);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            TryDeleteTemp(temp);
            WarnReframeFailed(stored, error);
            return false;
        }
        if (written is not { } result)
        {
            TryDeleteTemp(temp);
            return false;
        }
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TryDeleteTemp(temp);
            WarnReframeFailed(stored, error);
            return false;
        }
        DropStaleIndex(path);
        DropParsed(path);
        // 新布局的索引当场写出来: 否则下一次打开又得整份解析一遍才知道帧边界在哪。
        // 重切是按行切的, 打包记录可能被切在两个帧之间, 那里的差分计数是 0 —— 写进索引前先合并。
        SessionLogIndex.Append(path, SessionLogIndex.Coalesce(result.Entries), stored.Events.Count, stored.Events[0].Seq);
        _warn?.Invoke(
            $"session \"{stored.Meta.Id}\": the log was re-framed to the write budget "
            + $"({result.Length / 1024}KB, {stored.Events.Count} events kept), "
            + "so a history page now decompresses one frame instead of tens of megabytes");
        return true;
    }

    private void WarnReframeFailed(StoredLog stored, Exception error)
        => _warn?.Invoke(
            $"session \"{stored.Meta.Id}\": the log could not be re-framed to the write budget ({error.Message}); reading its history stays slow");

    /** 一帧在文件里的位置, 以及它装的事件段(来自索引)。 */
    private readonly record struct FrameSlot(ZstdFrames.FrameRange Range, long FirstSeq, int EventCount, long MinReplaceStart);

    /**
     * 索引与日志、事件列表三方对上的帧布局; 任一方对不上就返回 null(调用方退回整体重切)。只读帧头, 不解压。
     */
    private IReadOnlyList<FrameSlot>? FrameLayout(string path, StoredLog stored)
    {
        if (SessionLogIndex.TryLoad(path) is not { } index || index.Frames.Count < 2)
            return null;
        long[]? boundaries;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, LiveLogReadShare))
            boundaries = ZstdFrames.ScanFrameBoundaries(stream);
        if (boundaries is null || boundaries.Length != index.Frames.Count + 1)
            return null;
        var slots = new List<FrameSlot>(index.Frames.Count);
        var nextSeq = stored.Events[0].Seq;
        for (var slot = 0; slot < index.Frames.Count; slot += 1)
        {
            var entry = index.Frames[slot];
            if (entry.Start != boundaries[slot] || entry.End != boundaries[slot + 1] || entry.End > int.MaxValue)
                return null;
            if (slot > 0)
            {
                if (entry.FirstSeq != nextSeq)
                    return null;
                nextSeq += entry.EventCount;
            }
            slots.Add(new FrameSlot(new ZstdFrames.FrameRange((int)entry.Start, (int)entry.End), entry.FirstSeq,
                entry.EventCount, entry.MinReplaceStart));
        }
        return nextSeq == stored.Events[0].Seq + stored.Events.Count ? slots : null;
    }

    /**
     * 取一段**日志字节**: 头帧 + 覆盖 [fromEvent, fromEvent + maxEvents) 的事件帧, 一个字节都不解压。
     *
     * 这是"把历史交给别的进程"的成本下界: 宿主不再物化事件、也不再序列化成 JSON, 交出去的就是磁盘上那几帧。
     * 索引缺失时先走一次全量解析(顺手重建索引), 之后就是按帧切字节。
     */
    public SessionLogSlice? TryReadLogSlice(SessionId id, long fromEvent, int maxEvents)
    {
        if (_compression != JsonlCompression.Zstd)
            return null;
        var path = FindLog(id);
        if (path is null)
            return null;
        var index = SessionLogIndex.TryLoad(path);
        if (index is null)
        {
            // 索引缺失/与文件不符: 全量解析一次把它重建出来(此后都走按帧切字节)。
            _ = ReadStoredLog(path, id);
            index = SessionLogIndex.TryLoad(path);
        }
        if (index is not { Frames.Count: > 1 })
            return null;
        var total = index.EventCount;
        if (total <= 0)
            return null;
        var start = Math.Clamp(fromEvent, 0, total);
        var count = maxEvents > 0 ? Math.Min(maxEvents, total - start) : total - start;
        var (from, to) = index.FramesCovering(start, count);
        if (from < 0)
            return null;
        var frames = new List<FrameEntry>(to - from + 2) { index.Frames[0] };
        for (var slot = from; slot <= to; slot += 1)
            frames.Add(index.Frames[slot]);
        byte[] bytes;
        try
        {
            bytes = ReadFrameRanges(path, frames, new int[frames.Count], out _);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var header = Stat(id)?.Header ?? throw new SessionPersistenceNotFoundException(id);
        var eventCount = frames.Skip(1).Sum(frame => frame.EventCount);
        return new SessionLogSlice(bytes, header, frames[1].FirstSeq, eventCount, total, SessionLogSlice.HashOf(bytes));
    }

    /**
     * 逐帧重写: **没超预算的帧原样搬运压缩字节**, 只有超预算的帧才解压、按行重新切帧。
     *
     * 一份日志因此只有出问题的那几帧付解压/压缩的代价, 也不会为了一份副本把整份日志物化进内存:
     * 每次读进内存的只有一帧。返回 null 表示没有需要重切的帧(日志本来合规)。
     */
    private (long Length, List<FrameEntry> Entries)? WriteReframedLog(
        string path, string temp, IReadOnlyList<FrameSlot> layout, long budget)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, LiveLogReadShare);
        using var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        // 头帧的明文是解压任何一帧的前提(扫描器要靠它认出会话头), 只在这里解一次。
        var headerPlaintext = ZstdFrames.DecompressFrame(ReadFrame(source, layout[0].Range), 0);
        var entries = new List<FrameEntry>(layout.Count);
        var reframed = false;
        for (var slot = 0; slot < layout.Count; slot += 1)
        {
            var current = layout[slot];
            var frame = ReadFrame(source, current.Range);
            var plaintextBytes = ZstdFrames.TryReadPlaintextSize(frame, out var declared) ? declared : frame.LongLength;
            if (slot == 0 || plaintextBytes <= budget)
            {
                WriteFrame(target, entries, frame, current);
                continue;
            }
            reframed = true;
            foreach (var piece in ResegmentFrame(headerPlaintext, frame, current.FirstSeq))
            {
                var offset = target.Position;
                target.Write(piece.Content);
                entries.Add(new FrameEntry(offset, offset + piece.Content.Length, piece.FirstSeq, piece.EventCount)
                {
                    MinReplaceStart = piece.MinReplaceStart,
                });
            }
        }
        if (!reframed)
            return null;
        target.Flush(true);
        return (target.Length, entries);
    }

    private static void WriteFrame(Stream target, List<FrameEntry> entries, byte[] frame, FrameSlot slot)
    {
        var offset = target.Position;
        target.Write(frame);
        entries.Add(new FrameEntry(offset, offset + frame.Length, slot.FirstSeq, slot.EventCount)
        {
            MinReplaceStart = slot.MinReplaceStart,
        });
    }

    private static byte[] ReadFrame(Stream stream, ZstdFrames.FrameRange range)
    {
        var frame = new byte[range.End - range.Start];
        stream.Position = range.Start;
        stream.ReadExactly(frame);
        return frame;
    }

    /** 只解压这一帧, 按写盘预算重新切帧: 解出来的事件带着自己的绝对 seq, 重切不会串号。 */
    private List<(byte[] Content, long FirstSeq, int EventCount, long MinReplaceStart)> ResegmentFrame(
        ReadOnlySpan<byte> headerPlaintext, byte[] frame, long firstSeq)
    {
        var scanner = new SessionLogScanner(headerPlaintext, firstSeq);
        scanner.Write(ZstdFrames.DecompressFrame(frame, 0));
        var scan = scanner.Finish();
        if (scan.Damage.Any || scan.Events.Count == 0)
            throw new FormatException($"frame starting at event {firstSeq} could not be decoded");
        return EncodeEventFrames(scan.Events);
    }

    private void TryDeleteTemp(string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void DropParsed(string path)
    {
        lock (_parsedGate)
        {
            _parsed.Remove(path);
            _parsedOrder.Remove(path);
        }
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
        // 帧边界不是"现在这套写入规则"切出来的日志不走"按索引开写": 那条路不解压, 也就没机会重新分帧。
        // 退回全量解析, 由 TryReframeToWriteBudget 按写盘预算重写一遍 —— 一次性代价, 换来之后翻页从秒级降到毫秒级。
        if (index.Frames.Skip(1).Any(frame => frame.End - frame.Start > WriteBatch.MaxPlaintextBytes))
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
        else if (SpliceSeqRegression(path, ref parsed))
            damage = parsed.Damage;
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

    /**
     * 领票并返回下一个可用序号: 起点以日志真实条数为准(见 SessionWriteTicket)。
     *
     * 另一个宿主正持着票时先等一会儿再试: 它只在"发号 + 追加"的这段里持票, 通常几微秒就放开了。
     * 等不到才把可重试异常交给调用方(事件尚未创建, 因此没有丢失)。
     */
    /** 日志当前字节长度; 没有日志返回 null。只做一次 stat, 不读内容。 */
    internal long? LogLength(SessionId id)
        => FindLog(id) is { } path && new FileInfo(path) is { Exists: true } info ? info.Length : null;

    /**
     * 领票取这一批事件的起点序号。
     *
     * 不在这里重试:"另一个活着的写者正持有票"不是可等待的瞬时状态, 它可能持续到对方落盘为止 ——
     * 反复 sleep 只会把调用方(常常是界面线程)按住, 而事件本身还没创建, 交回上层缓冲是正确做法。
     * 真正的瞬时竞争只有锁文件本身, 那一层已经有自己的短重试(见 SessionWriteTicket.TryLock)。
     */
    internal long NextSeq(SessionHeader header, int count, long knownCount)
    {
        EnsureRootEncoding();
        var path = SessionWriteTicket.PathFor(_root, header.Cwd, header.Id);
        // 只信索引与句柄自己的游标: 整份读日志会撞上别人（或自己）正占着的尾锁, 发号因此卡死过。
        var logPath = FindLog(header.Id);
        var stored = Math.Max(knownCount, logPath is null ? 0 : SessionLogIndex.TryLoad(logPath)?.EventCount ?? 0);
        try
        {
            return SessionWriteTicket.NextSeq(path, stored, count);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 票文件读不到只是这一次, 不要把裸 IO 异常扔进回合里。
            throw new SessionWriteBusyException(Environment.ProcessId,
                $"the write ticket could not be read ({error.GetType().Name}); retrying on the next write");
        }
    }

    /** 这批事件已经落盘到 durableUpTo(不含): 票随之前进, 没有未落盘事件时票被删掉。 */
    internal void MarkDurable(SessionHeader header, long durableUpTo)
        => SessionWriteTicket.MarkDurable(SessionWriteTicket.PathFor(_root, header.Cwd, header.Id), durableUpTo);

    /** 放弃自己手里的票(写句柄关闭)。 */
    internal void ReleaseTicket(SessionHeader header)
        => SessionWriteTicket.Release(SessionWriteTicket.PathFor(_root, header.Cwd, header.Id));

    /** 读回日志上 [from, to) 的事件: 别的写者写进去的那一段, 用于把内存对齐到磁盘。 */
    internal IReadOnlyList<SessionEvent> ReadStoredRange(SessionId id, long from, long to)
    {
        if (to <= from)
            return [];
        var path = FindLog(id) ?? throw new SessionPersistenceNotFoundException(id);
        var events = ReadStoredLog(path, id).Events;
        var start = (int)Math.Clamp(from, 0, events.Count);
        var end = (int)Math.Clamp(to, start, events.Count);
        return events.GetRange(start, end - start);
    }

    internal void PersistBatch(SessionHeader header, IReadOnlyList<SessionEvent> events, StorageHandleState state)
    {
        EnsureRootEncoding();
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
        state.WrittenLength = Materialize(header, state.InheritedEventCount, []);
        MarkMaterialized(header.Id);
    }

    /**
     * 把"写不进去"的那批事件另存一份(同一会话目录下), 并打一条能照做的日志。
     *
     * 租约的判据可以输(别人活着并且持有写权), 事件不能丢: 丢掉的是用户刚刚说的话。
     * 存成一行一条事件的 JSONL, 与日志并排, 事后可以人工补回或直接读。
     */
    internal void SaveUnsavedTail(SessionHeader header, IReadOnlyList<SessionEvent> events, Exception error)
    {
        if (events.Count == 0)
            return;
        try
        {
            var directory = JsonlLayout.SessionDir(_root, header.Cwd, header.Id);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory,
                $"session.unsaved-{Environment.ProcessId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.jsonl");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                foreach (var sessionEvent in events)
                    JsonSerializer.Serialize(stream, sessionEvent, DshJson.Info<SessionEvent>());
                stream.Flush(true);
            }
            _warn?.Invoke($"session \"{header.Id}\": {events.Count} unsaved event(s) were kept at {path} "
                + $"because the log could not be written ({error.GetType().Name}: {error.Message})");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke($"session \"{header.Id}\": {events.Count} unsaved event(s) were LOST — "
                + $"the recovery copy could not be written either ({failure.Message})");
        }
    }


    /**
     * 中段序号回退的修复: 同一个会话日志被两个写入者按各自的游标追加过, 于是文件里出现
     * "写到 638053 又回到 623136 重写一遍" 的形状。整份日志因此不再单调, 按页读会整段失败。
     *
     * 真相是序号: 保留 [0, 首次回退) 前缀, 再把回退之后那段**递增序列里仍在前缀之外**的事件接上,
     * 重复段丢掉。原文件先整份备份成 `*.damaged-时间戳`, 再无损重写; 任何一步不成立就不动文件。
     */
    private bool SpliceSeqRegression(string path, ref ParsedLog parsed)
    {
        var events = parsed.Events;
        var regression = -1;
        for (var index = parsed.InheritedEventCount > 0 ? (int)parsed.InheritedEventCount : 1; index < events.Count; index += 1)
        {
            if (events[index].Seq <= events[index - 1].Seq)
            {
                regression = index;
                break;
            }
        }
        if (regression < 0)
            return false;
        var prefix = events.GetRange(0, regression);
        var resume = -1;
        for (var index = regression; index < events.Count; index += 1)
        {
            if (index > regression && events[index].Seq <= events[index - 1].Seq)
                continue;
            if (events[index].Seq >= prefix.Count)
            {
                resume = index;
                break;
            }
        }
        var kept = resume < 0 ? prefix : [.. prefix, .. events.GetRange(resume, events.Count - resume)];
        var keptIsSound = kept.Count > 0;
        for (var index = 1; keptIsSound && index < kept.Count; index += 1)
            keptIsSound = kept[index].Seq == kept[index - 1].Seq + 1;
        if (!keptIsSound)
            return false;
        var dropped = events.Count - kept.Count;
        try
        {
            File.WriteAllBytes($"{path}.damaged-{DateTime.UtcNow:yyyyMMddHHmmss}", File.ReadAllBytes(path));
            var (content, _, _) = EncodeWholeLog(parsed.Meta, parsed.InheritedEventCount, kept);
            var temp = $"{path}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}.tmp";
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke(
                $"session \"{parsed.Meta.Id}\": the log's sequence numbers regress at event {regression} "
                + $"and the repair could not be written ({error.Message}); opening the readable prefix only");
            parsed = parsed with { Events = prefix, Damage = parsed.Damage.Plus(1, null) };
            return true;
        }
        SessionLogIndex.Delete(path);
        DropWindowReader(path);
        DropParsed(path);
        parsed = parsed with { Events = kept, TornTruncateTo = null, RecoveredTail = [], Damage = parsed.Damage.Plus(1, null) };
        _warn?.Invoke(
            $"session \"{parsed.Meta.Id}\": the log's sequence numbers regressed at event {regression} "
            + $"(a second writer rewrote an already-appended range); kept {kept.Count} event(s), dropped {dropped} duplicate(s), "
            + "the original file is preserved as *.damaged-*");
        return true;
    }

    /**
     * 派生缓存: 读上次存下的表面折叠结果, 并用"日志没变短、事件没变少"验真。
     *
     * 表面是派生状态, 折一遍要读整条日志(0.8–1.0M 事件实测 5–21s), 而它只有几百到几千个序号。
     * 缓存按 seq 列表存(zstd 帧, 差值 varint), 与日志并排放在 `*.surface` 里。
     */
    public SurfaceFoldSnapshot? TryLoadSurfaceFold(SessionId id)
    {
        if (_compression != JsonlCompression.Zstd || FindLog(id) is not { } path)
            return null;
        var sidecar = SurfaceFoldPath(path);
        try
        {
            if (!File.Exists(sidecar))
                return null;
            var bytes = File.ReadAllBytes(sidecar);
            var scan = ZstdFrames.Scan(bytes);
            if (scan.Frames.Count < 2 || scan.Damaged.Count > 0 || scan.TornStart is not null)
                return null;
            var headerText = Encoding.UTF8.GetString(
                ZstdFrames.DecompressFrame(bytes, scan.Frames[0].Start,
                    scan.Frames[0].End - scan.Frames[0].Start, scan.Frames[0].Start)).Trim();
            var parts = headerText.Split(' ');
            if (parts.Length != 6 || parts[0] != SurfaceFoldMagic
                || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out var length)
                || !long.TryParse(parts[2], CultureInfo.InvariantCulture, out var total)
                || !int.TryParse(parts[3], CultureInfo.InvariantCulture, out var generation)
                || !int.TryParse(parts[4], CultureInfo.InvariantCulture, out var count)
                || !int.TryParse(parts[5], CultureInfo.InvariantCulture, out var version)
                || version != SurfaceFoldVersion)
                return null;
            var plain = new List<byte>();
            for (var slot = 1; slot < Math.Min(scan.Frames.Count, 2); slot += 1)
            {
                var frame = scan.Frames[slot];
                plain.AddRange(ZstdFrames.DecompressFrame(bytes, frame.Start, frame.End - frame.Start, frame.Start));
            }
            var nodes = DecodeSurfaceNodes([.. plain], count);
            // 第三帧(如果有)是 request/header 与 request/context 的折叠值: 老检查点没有这一帧, 读到就是没有。
            var (snapshotHeader, snapshotContext) = scan.Frames.Count > 2
                ? DecodeSurfaceMeta(ZstdFrames.DecompressFrame(bytes,
                    scan.Frames[2].Start, scan.Frames[2].End - scan.Frames[2].Start, scan.Frames[2].Start))
                : (null, null);
            var snapshot = new SurfaceFoldSnapshot(nodes, length, total, generation, snapshotHeader, snapshotContext);
            var currentLength = new FileInfo(path).Length;
            var currentTotal = WindowReader(path)?.TotalEvents ?? total;
            return snapshot.Fits(currentLength, currentTotal) ? snapshot : null;
        }
        catch (Exception error) when (error is IOException or FormatException or UnauthorizedAccessException)
        {
            _warn?.Invoke($"session \"{id}\": the surface fold cache could not be read ({error.GetType().Name}: {error.Message}); folding the log instead");
            return null;
        }
    }

    /** 存下表面折叠结果: 先写临时文件再原子替换, 写不成就不写(缓存永远只是加速, 不是真相)。 */
    public void SaveSurfaceFold(SessionId id, SurfaceFoldSnapshot fold)
    {
        if (_compression != JsonlCompression.Zstd || fold.Nodes.Count > MaxCachedSurfaceNodes
            || FindLog(id) is not { } path)
            return;
        try
        {
            // 长度与条数一律以**日志**为准: 会话可能比落盘多几条待写事件, 拿会话的数字会让缓存永远对不上。
            var logLength = new FileInfo(path).Length;
            var totalEvents = WindowReader(path)?.TotalEvents ?? fold.TotalEvents;
            var header = string.Create(CultureInfo.InvariantCulture,
                $"{SurfaceFoldMagic} {logLength} {totalEvents} {fold.ReplaceGeneration} {fold.Nodes.Count} {SurfaceFoldVersion}");
            var headerFrame = ZstdFrames.CompressFrame(Encoding.UTF8.GetBytes(header + "\n"));
            var payloadFrame = ZstdFrames.CompressFrame(EncodeSurfaceNodes(fold.Nodes));
            // 尾部那条尾部信息(request/header 与 request/context 的折叠值)自带一帧: 老版本没有这一帧, 读到就是没有。
            var metaFrame = ZstdFrames.CompressFrame(EncodeSurfaceMeta(fold));
            var sidecar = SurfaceFoldPath(path);
            var temp = $"{sidecar}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}.tmp";
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(headerFrame);
                stream.Write(payloadFrame);
                stream.Write(metaFrame);
                stream.Flush(true);
            }
            File.Move(temp, sidecar, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string SurfaceFoldPath(string logPath) => $"{logPath}.surface";

    /** 日志变短(回退/修复)时作废派生缓存: 旧快照描述的是更长的日志。 */
    public void DropSurfaceFold(SessionId id)
    {
        if (FindLog(id) is { } path)
            File.Delete(SurfaceFoldPath(path));
    }

    /** 检查点尾部信息: request/header 与 request/context 的折叠值(没有就是 null)。 */
    private static byte[] EncodeSurfaceMeta(SurfaceFoldSnapshot fold)
    {
        var meta = new SurfaceFoldMeta(fold.RequestHeader, fold.RequestContext);
        return JsonSerializer.SerializeToUtf8Bytes(meta, DshCoreJsonContext.Default.SurfaceFoldMeta);
    }

    private static (EpochHeader? Header, RequestContextPayload? Context) DecodeSurfaceMeta(byte[] payload)
    {
        try
        {
            var meta = JsonSerializer.Deserialize(payload, DshCoreJsonContext.Default.SurfaceFoldMeta);
            return (meta?.RequestHeader, meta?.RequestContext);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /** 表面节点序号按差值 varint 存: 相邻序号通常只差几个到几百个。 */
    private static byte[] EncodeSurfaceNodes(IReadOnlyList<long> nodes)
    {
        var buffer = new List<byte>(nodes.Count * 2);
        long previous = 0;
        foreach (var seq in nodes)
        {
            var delta = seq - previous;
            previous = seq;
            var value = (ulong)delta;
            while (value >= 0x80)
            {
                buffer.Add((byte)(value | 0x80));
                value >>= 7;
            }
            buffer.Add((byte)value);
        }
        return [.. buffer];
    }

    private static List<long> DecodeSurfaceNodes(byte[] payload, int count)
    {
        var nodes = new List<long>(count);
        long previous = 0;
        var cursor = 0;
        while (cursor < payload.Length && nodes.Count < count)
        {
            ulong value = 0;
            var shift = 0;
            while (cursor < payload.Length)
            {
                var current = payload[cursor];
                cursor += 1;
                value |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                    break;
                shift += 7;
            }
            previous += (long)value;
            nodes.Add(previous);
        }
        if (nodes.Count != count)
            throw new FormatException("surface fold sidecar is truncated");
        return nodes;
    }

    /**
     * 就地修掉"末尾不属于任何事件"的字节, 返回修完之后的事件条数。
     *
     * 不等到下一次打开: 宿主可能连续开着几天, 那期间一条都不写盘是绝对不能接受的。
     * 读得出来的记录全部保留, 原文件另存为 `*.damaged-*`(取证用), 主日志按可读前缀整份重写。
     * 修不动(IO 失败)时抛出去, 由调用方按"这次先别写"处理 —— 事件仍在缓冲里。
     */
    internal long RepairDamagedTail(SessionId id)
    {
        if (FindLog(id) is not { } path)
            return 0;
        var stored = ReadStoredLog(path, id);
        if (!stored.Damage.Any && stored.TornTruncateTo is { } keep)
        {
            // 末尾半帧(上一次写盘崩在半路): 截掉即可, 不留取证副本也不告警 —— 那是正常恢复, 不是异常。
            TruncateTornTail(stored.Meta, keep);
            SessionLogIndex.Delete(path);
            DropWindowReader(path);
        }
        else
        {
            // 读不出的记录/无法归属的字节: 原文件另存一份(`*.damaged-*`)再从可读前缀重写。
            RepairDamagedLog(path, stored);
        }
        return StoredEventCount(id);
    }

    internal void TruncateTornTail(SessionHeader header, long truncateTo)
    {
        var path = JsonlLayout.LogPath(_root, header.Cwd, header.Id, _compression);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.SetLength(truncateTo);
        stream.Flush(true);
    }


    internal void ReleaseHandle(JsonlSessionHandle handle, bool materialized)
    {
        lock (_trackerGate)
        {
            _openHandles.Remove(handle);
            if (handle.Access != SessionAccess.Write) return;
            _writers.Remove(handle.Id);
            if (!materialized) _pending.Remove(handle.Id);
        }
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
        // 打包记录跨帧时那一帧的差分计数是 0: 合并成整数条记录的帧, 否则索引校验不过、切字节会切出半条记录。
        return SessionLogIndex.Coalesce(entries);
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

    /**
     * 追加一批事件。整个动作是"看末尾 → 定起点 → 写 → 更新索引"的一次事务, 临界区用**锁文件**占住,
     * 另一个写者只能等这一次追加结束 —— 否则两边会各自把同一个起点当成自己的,
     * 把两套序号写进同一段字节(那正是"日志再也读不回来"的来源)。
     *
     * 不用日志自身的字节区间锁: 那会连带拒绝对该区间的读(Windows 上是 ERROR_LOCK_VIOLATION, 同进程也算冲突),
     * 于是写者一追加, 正在读尾部的读者就失败 —— 那是把并发读者挡在外面, 不是"只挡写者"。
     * 代价是每次追加多一次锁文件开关; 读者因此完全不必等写者。
     */
    private void AppendLines(SessionHeader meta, IReadOnlyList<SessionEvent> events, StorageHandleState state)
    {
        var frames = EncodeEventFrames(events);
        var path = JsonlLayout.LogPath(_root, meta.Cwd, meta.Id, _compression);
        long total = 0;
        foreach (var frame in frames)
            total += frame.Content.Length;
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        var (guard, start) = AcquireAppendTail(stream, path);
        var before = start;
        try
        {
            // 这一批的号必须接在日志真实末尾之后(索引级复核; 没有索引的日志由上一步的长度比对兜住)。
            if (events.Count > 0 && SessionLogIndex.TryLoad(path) is { } tail && tail.Length == before
                && tail.EventCount != events[0].Seq)
            {
                throw new SessionNumberStaleException(tail.EventCount, events[0].Seq);
            }
            // 日志在本写入者两次落盘之间被推进过: 以磁盘为准对齐, 而不是永久停写。
            before = ReconcileLocked(meta, path, before, events, state);
            var entries = new List<FrameEntry>(frames.Count);
            long offset = before;
            foreach (var frame in frames)
            {
                entries.Add(new FrameEntry(offset, offset + frame.Content.Length, frame.FirstSeq, frame.EventCount)
                {
                    MinReplaceStart = frame.MinReplaceStart,
                });
                offset += frame.Content.Length;
            }
            try
            {
                // 显式落到锁住的起点: FileMode.Append 会跟着当时的文件末尾走, 抢过别人的位置也不报错。
                stream.Seek(before, SeekOrigin.Begin);
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
        finally
        {
            guard.Dispose();
        }
    }

    /**
     * 占住这次追加, 返回真正的起点。
     *
     * 拿锁前后各看一次长度: 别人正好在我读长度之后写完的话, 我锁住的是旧起点,
     * 那时放锁重来, 直到"我持锁期间长度没有变化"为止。
     */
    private (AppendTailGuard Guard, long Start) AcquireAppendTail(FileStream stream, string path)
    {
        for (var attempt = 0; attempt < TailLockAttempts; attempt += 1)
        {
            var start = stream.Length;
            if (TryEnterAppendTail(path) is not { } guard)
            {
                // 有人正在追加: 等他放锁再看新的末尾。
                Thread.Sleep(TailLockRetryDelay);
                continue;
            }
            if (stream.Length == start)
                return (guard, start);
            guard.Dispose();
        }
        // 写不进去不是"日志坏了": 按"写权此刻不在我手里"处理(留缓冲、下次重试), 不要把它当作报废。
        throw new SessionWriteBusyException(Environment.ProcessId,
            "another writer kept the session log tail busy; retrying on the next write");
    }

    /**
     * 进临界区: 日志旁边的锁文件, 全平台一致。
     *
     * 这里曾经在非 macOS 上用日志文件自身的**字节区间锁**(一个 syscall)。那是错的:
     * Windows 的字节区间锁会连带拒绝对该区间的**读**, 而且跨进程、同进程不同句柄都算冲突 ——
     * 于是"写者正在追加尾部"与"读者正在读尾部"必然互相打断, 读者拿到的是
     * "另一个程序已锁定文件的一部分"(ERROR_LOCK_VIOLATION)。打开参数上的共享位救不了它,
     * 因为这不是共享冲突而是区域锁。
     *
     * 锁文件只在写者之间争用, 读者完全看不到它; 文件名不带 session 前缀, 不会被日志/索引的枚举当成日志。
     */
    private static AppendTailGuard? TryEnterAppendTail(string path)
    {
        try
        {
            return new AppendTailGuard(new FileStream(TailLockPath(path), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /** 追加临界区的锁文件: 与日志同目录, 名字与日志前缀无关, 免得被"找日志"的枚举扫到。 */
    private static string TailLockPath(string path)
        => Path.Join(Path.GetDirectoryName(path), "append.tail.lock");

    /** 追加临界区: 锁文件, 释放即关闭。 */
    private sealed class AppendTailGuard(FileStream lockFile) : IDisposable
    {
        public void Dispose() => lockFile.Dispose();
    }


    /**
     * 尾部长度与"我上次写完的长度"不一致时的对齐。
     *
     * 只有一种情形能在本层就地解决: 索引覆盖到文件末尾, 且它的条数正好等于这批事件的起点 ——
     * 那说明日志里已经是"这一批之前"的全部事件(上一批其实写成功了), 顺着写就行。
     * 其余情形(尾巴上有没有归属的字节、条数对不上)不在这里猜: 由调用方按会话重新对齐后整段补写,
     * 猜错的代价是把用户的事件丢进别人的日志里, 比多跑一趟恢复流程贵得多。
     */
    private long ReconcileLocked(SessionHeader meta, string path, long before, IReadOnlyList<SessionEvent> events, StorageHandleState state)
    {
        if (state.WrittenLength is not { } expected || expected == before)
            return before;
        var firstSeq = events.Count > 0 ? events[0].Seq : 0;
        if (SessionLogIndex.TryLoad(path) is { } index && index.Length == before && index.EventCount == firstSeq)
        {
            state.Cursor = index.EventCount;
            state.WrittenLength = before;
            return before;
        }
        throw new SessionPersistenceCorruptionException(
            $"session \"{meta.Id}\": the log changed from {expected} to {before} bytes while this writer held it, "
            + $"and its frames do not line up with the next event #{firstSeq}");
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
        lock (_trackerGate) _writers.Remove(id);
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
