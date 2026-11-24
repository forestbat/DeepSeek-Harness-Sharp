using System.Diagnostics;
using Dsh.Core;

namespace Dsh.Persistence;

/**
 * 窗口读取的分阶段计数: 只给取证与探针读, 生产路径不读。
 * 分开量是因为它们各有各的代价来源: 读字节受 I/O 与帧大小影响, 解压受帧数与压缩比影响,
 * 解析物化受事件数与 payload 体积影响 —— "为什么这一档慢了"只能看这三段各占多少。
 */
internal static class WindowReadStats
{
    private static long _readMilliseconds;
    private static long _decompressMilliseconds;
    private static long _decodeMilliseconds;
    private static long _compressedBytes;
    private static long _plaintextBytes;
    private static long _events;
    private static long _framesDecoded;
    private static long _scanMilliseconds;

    internal static void Reset()
    {
        Interlocked.Exchange(ref _readMilliseconds, 0);
        Interlocked.Exchange(ref _decompressMilliseconds, 0);
        Interlocked.Exchange(ref _decodeMilliseconds, 0);
        Interlocked.Exchange(ref _compressedBytes, 0);
        Interlocked.Exchange(ref _plaintextBytes, 0);
        Interlocked.Exchange(ref _events, 0);
        Interlocked.Exchange(ref _framesDecoded, 0);
        Interlocked.Exchange(ref _scanMilliseconds, 0);
    }

    /** 切帧(只读帧头, 不解压)的累计毫秒: 没索引的老日志在这里付一次全文件扫描。 */
    internal static void AddScan(long milliseconds) => Interlocked.Add(ref _scanMilliseconds, milliseconds);

    internal static void Add(long readMs, long decompressMs, long decodeMs, long compressed, long plaintext,
        long events, long frames)
    {
        Interlocked.Add(ref _readMilliseconds, readMs);
        Interlocked.Add(ref _decompressMilliseconds, decompressMs);
        Interlocked.Add(ref _decodeMilliseconds, decodeMs);
        Interlocked.Add(ref _compressedBytes, compressed);
        Interlocked.Add(ref _plaintextBytes, plaintext);
        Interlocked.Add(ref _events, events);
        Interlocked.Add(ref _framesDecoded, frames);
    }

    internal static string Describe() => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        "切帧 {0}ms, 读字节 {1}ms / {2:N1}MB, 解压 {3}ms / 明文 {4:N1}MB, 解析物化 {5}ms / {6} 事件 ({7} 帧)",
        Interlocked.Read(ref _scanMilliseconds),
        Interlocked.Read(ref _readMilliseconds),
        Interlocked.Read(ref _compressedBytes) / 1024d / 1024d,
        Interlocked.Read(ref _decompressMilliseconds),
        Interlocked.Read(ref _plaintextBytes) / 1024d / 1024d,
        Interlocked.Read(ref _decodeMilliseconds),
        Interlocked.Read(ref _events),
        Interlocked.Read(ref _framesDecoded));
}

/**
 * 按帧切窗读: 帧边界只按帧头扫(不解压), 只有命中的字节区间才解压, 学到一帧的 seq 区间就记住。
 *
 * 现场老日志没有 `.frames` 索引, 而建索引要整份解压(1M 事件约 16 秒)。
 * 这里把"打开"变成"读一遍文件切帧(不解压) + 解压尾窗那一小段", 后续翻页再按需解压。
 * 有索引时直接用索引里的帧与 seq 区间, 少一次切帧扫描。
 *
 * 老日志的帧可能小到几百字节(上百万帧), 所以读窗口按**字节预算**取整段、一次解压,
 * 而不是逐帧开文件解压 —— 后者会把"取一页"变成几千次文件打开。
 *
 * 任何结构疑点(坏帧、行解不出来、帧内 seq 不连续)一律抛 FormatException:
 * 调用方据此退回全量解析, 由它去修/降级, 而不是让窗口读把损伤掩盖成"少了几条事件"。
 */
internal sealed class SessionLogWindowReader
{
    private const FileShare ReadShare = FileShare.ReadWrite | FileShare.Delete;

    /** 核对帧边界时抽查的帧数上限: 边界若整体错位, 抽样一定落在中间某处; 全量核对在大日志上是几万次 seek。 */
    private const int SampleFrames = 32;

    private readonly string _path;
    private readonly Lock _gate = new();
    /** 每帧起点, 末项等于已扫到的文件长度。 */
    private readonly List<long> _starts;
    /** 进程内共享的那份帧索引(整段都来自它时不重建 `_known`); 扫描出来的新增段没有索引可依。 */
    private SessionLogIndex? _index;
    private readonly Dictionary<int, FrameEntry> _known = [];
    private long? _total;

    private SessionLogWindowReader(string path, List<long> starts, SessionLogIndex? index)
    {
        _path = path;
        _starts = starts;
        _index = index;
    }

    public static SessionLogWindowReader? TryOpen(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, ReadShare);
            var length = stream.Length;
            if (length == 0)
                return null;
            if (SessionLogIndex.TryLoad(path) is { Frames.Count: > 1 } index && index.Length == length)
            {
                var indexed = StartsOf(index);
                // 结构校验(首帧 0 起、末帧到文件尾、seq 连续)挡不住"整份日志平移过"的索引:
                // 帧边界要落在真正的帧头上才算数, 否则窗口读会按错位的边界解压(实测首页读不回来)。
                if (SampledStartsAreFrames(stream, indexed))
                    return new SessionLogWindowReader(path, indexed, index);
                SessionLogIndex.Delete(path);
                return null;
            }
            var scanClock = Stopwatch.StartNew();
            var scanned = ZstdFrames.ScanFrameBoundaries(stream);
            WindowReadStats.AddScan(scanClock.ElapsedMilliseconds);
            if (scanned is not { Length: > 1 } boundaries
                || boundaries[0] != 0 || boundaries[^1] != length)
                return null;
            return new SessionLogWindowReader(path, [.. boundaries], null);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /** 抽查帧边界确实落在帧头上(索引可能整份平移过: 头帧被改写时长度会变)。 */
    private static bool SampledStartsAreFrames(Stream stream, List<long> starts)
    {
        var frames = starts.Count - 1;
        if (frames <= 0 || !ZstdFrames.StartsWithFrame(stream, starts[0]))
            return false;
        var step = Math.Max(1, frames / SampleFrames);
        for (var frame = 1; frame < frames; frame += step)
            if (!ZstdFrames.StartsWithFrame(stream, starts[frame]))
                return false;
        return ZstdFrames.StartsWithFrame(stream, starts[frames - 1]);
    }

    /** 索引就是帧边界的权威列表: 起点逐个抄出来, 末项补上文件长度。 */
    internal static List<long> StartsOf(SessionLogIndex index)
    {
        var starts = new List<long>(index.Frames.Count + 1);
        for (var position = 0; position < index.Frames.Count; position += 1)
            starts.Add(index.Frames[position].Start);
        starts.Add(index.Length);
        return starts;
    }

    /** 索引里的帧边界是否仍落在日志的帧头上: 整份平移过的索引(头帧被改写后没跟着平移)不再可信。 */
    public static bool Matches(Stream stream, SessionLogIndex index)
        => SampledStartsAreFrames(stream, StartsOf(index));

    /**
     * 活会话在追加: 只扫新增的那一段。
     * 文件变短(截断/重写)时返回 false, 调用方重新 TryOpen。
     */
    public bool TryExtend()
    {
        lock (_gate)
        {
            long length;
            try
            {
                length = new FileInfo(_path).Length;
            }
            catch (IOException)
            {
                return false;
            }
            if (length == _starts[^1])
                return true;
            if (length < _starts[^1])
                return false;
            // 索引跟着日志一起长了(写句柄或别的进程追加过): 直接采信, 不必再切一遍帧。
            if (SessionLogIndex.TryLoad(_path) is { Frames.Count: > 1 } grown
                && grown.Length == length && (_index is null || grown.Length > _index.Length))
            {
                Adopt(grown);
                return true;
            }
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, ReadShare);
            if (ZstdFrames.ScanFrameBoundaries(stream, _starts[^1]) is not { Length: > 1 } boundaries
                || boundaries[^1] != length)
                return false;
            _starts.AddRange(boundaries.Skip(1));
            _total = null;
            return true;
        }
    }

    private void Adopt(SessionLogIndex index)
    {
        _starts.Clear();
        _starts.AddRange(StartsOf(index));
        _index = index;
        _known.Clear();
        _total = null;
    }

    private int FrameCount => _starts.Count - 1;

    /** 总事件数: 学最后一帧就能算出来(最后一条 seq + 1)。 */
    public long TotalEvents
    {
        get
        {
            lock (_gate)
            {
                if (_total is { } known)
                    return known;
                var entry = LearnLocked(FrameCount - 1);
                _total = entry.EventCount == 0 ? 0 : entry.FirstSeq + entry.EventCount;
                return _total.Value;
            }
        }
    }

    /**
     * 尾部窗口: 从末帧按**索引里的事件条数**往回数, 攒够 count 条就解压这一段。
     *
     * 不按字节预估: 一帧装多少事件只由索引的 EventCount 说了算, 而"多少个事件对应多少字节"
     * 取决于帧内密度 —— 大工具输出与小 chunk 混排时两者相差两个数量级(实测尾窗因此退化成整份解压)。
     * 按条数往回数是精确的: 索引命中时零解压(直接读 EventCount), 未建索引时每帧解压一次并缓存。
     */
    public (long From, IReadOnlyList<SessionEvent> Events)? ReadTail(int count)
    {
        if (count <= 0)
            return null;
        lock (_gate)
        {
            var total = TotalEventsLocked();
            if (total == 0)
                return null;
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, ReadShare);
            // 第 0 帧是头行, 不含事件: 往回数最多退到这里。
            var firstEventFrame = Math.Min(1, FrameCount - 1);
            var start = FrameCount - 1;
            var accumulated = (long)LearnLocked(start, stream).EventCount;
            while (accumulated < count && start > firstEventFrame)
            {
                start -= 1;
                accumulated += LearnLocked(start, stream).EventCount;
            }

            var events = DecodeFrameRange(_starts[start], _starts[^1], stream);
            return events.Count == 0 ? null : (events[0].Seq, events);
        }
    }

    /** 读 [offset, offset + count): 先二分定位覆盖 offset 的帧, 再按**事件条数**往后扩到够。 */
    public (long From, IReadOnlyList<SessionEvent> Events)? Read(long offset, int count)
    {
        if (offset < 0 || count <= 0)
            return null;
        lock (_gate)
        {
            var frame = FrameContainingLocked(offset);
            if (frame < 0)
                return null;
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, ReadShare);
            var end = offset + count;
            // 一帧给不出这个区间(帧比页还小)时按索引条数往后扩, 不用字节猜测。
            var last = frame;
            while (last + 1 < FrameCount)
            {
                var entry = LearnLocked(last, stream);
                if (entry.EventCount > 0 && entry.FirstSeq + entry.EventCount >= end)
                    break;
                last += 1;
            }

            var events = DecodeFrameRange(_starts[frame], _starts[last + 1], stream);
            var filtered = events
                .Where(sessionEvent => sessionEvent.Seq >= offset && sessionEvent.Seq < end)
                .ToList();
            return filtered.Count == 0 ? null : (filtered[0].Seq, filtered);
        }
    }

    private long TotalEventsLocked()
    {
        if (_total is { } known)
            return known;
        var entry = LearnLocked(FrameCount - 1);
        _total = entry.EventCount == 0 ? 0 : entry.FirstSeq + entry.EventCount;
        return _total.Value;
    }

    private int FrameContainingLocked(long seq)
    {
        var low = 1;
        var high = FrameCount - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var entry = LearnLocked(middle);
            if (entry.EventCount == 0 || seq < entry.FirstSeq)
            {
                high = middle - 1;
                continue;
            }
            if (seq >= entry.FirstSeq + entry.EventCount)
            {
                low = middle + 1;
                continue;
            }
            return middle;
        }
        return -1;
    }

    private List<SessionEvent> DecodeFrameRange(long start, long end, FileStream? shared = null)
    {
        var plain = ReadFramePlain(start, end, shared);
        var decodeClock = Stopwatch.StartNew();
        var events = DecodeLines(plain, start);
        var decodeMilliseconds = decodeClock.ElapsedMilliseconds;
        WindowReadStats.Add(0, 0, decodeMilliseconds, 0, 0, events.Count, 0);
        return events;
    }

    /** 只把一帧读进来并解压, 不解码成事件: 学条数只需要明文。 */
    private byte[] ReadFramePlain(long start, long end, FileStream? shared)
    {
        var buffer = new byte[end - start];
        var readClock = Stopwatch.StartNew();
        // 同一次公开读里复用同一个句柄: 逐帧 new FileStream 在"没有索引 + 细帧"的日志上就是几万次文件打开,
        // 实测 69,608 次打开要 5.4 s, 而裸读全部字节只要 21 ms —— 打开本身才是 I/O 侧的真开销。
        if (shared is null)
        {
            using var own = new FileStream(_path, FileMode.Open, FileAccess.Read, ReadShare);
            own.Position = start;
            own.ReadExactly(buffer, 0, buffer.Length);
        }
        else
        {
            shared.Position = start;
            shared.ReadExactly(buffer, 0, buffer.Length);
        }

        var readMilliseconds = readClock.ElapsedMilliseconds;
        var decompressClock = Stopwatch.StartNew();
        var plain = ZstdFrames.DecompressFrame(buffer, 0, buffer.Length, (int)start);
        WindowReadStats.Add(readMilliseconds, decompressClock.ElapsedMilliseconds, 0, buffer.Length, plain.Length, 0, 1);
        return plain;
    }

    /**
     * 用索引把"尾窗要退到哪"一次算准: 攒够 count 条事件即可。
     *
     * **不看 replace 指向哪里** —— 那是 B5 的关键: 跨边界的 replace 不再要求把远端节点塞进窗口,
     * 折叠到它时按页取(Session 把 PageCache 交给 Surface.Manager)。原来按 replace 目标退窗口,
     * 对 d 档这类"replace 指向 seq≈0"的日志等于整份解压。
     */
    public long? ResolveTailStart(int count)
    {
        if (count <= 0 || _index is not { Frames.Count: > 0 })
            return null;
        lock (_gate)
        {
            var start = FrameCount - 1;
            long accumulated = 0;
            while (start >= 1)
            {
                var entry = LearnLocked(start);
                accumulated += entry.EventCount;
                if (accumulated >= count)
                    return entry.FirstSeq;
                start -= 1;
            }
            return 1;
        }
    }

    private FrameEntry LearnLocked(int frame, FileStream? shared = null)
    {
        // 索引里已有的帧不必解压去学: 它就是从同一份日志切出来的。
        if (_index is { } index && frame < index.Frames.Count)
            return index.Frames[frame];
        if (_known.TryGetValue(frame, out var known))
            return known;
        var start = _starts[frame];
        var end = _starts[frame + 1];
        FrameEntry entry;
        if (frame == 0)
        {
            // 第 0 帧是头行, 不含事件。
            entry = new FrameEntry(start, end, 0, 0);
        }
        else
        {
            // 学"这帧有几条事件"只解压 + 扫描, 不物化 payload:
            // 走完整解码会把整份日志的事件对象建一遍, 之后取窗口再建一遍(实测 327,855 条解了两遍)。
            var plain = ReadFramePlain(start, end, shared);
            var (measuredSeq, measuredCount) = ChunkRows.MeasureStorageRecords(plain);
            if (measuredCount == 0)
                throw new FormatException($"session log frame at byte {start} holds no events");
            entry = new FrameEntry(start, end, measuredSeq, measuredCount);
        }
        // 借共享句柄学到的帧照样缓存: 下次没有句柄时也能直接命中, 不必再解压。
        _known[frame] = entry;
        return entry;
    }

    private static List<SessionEvent> DecodeLines(byte[] plain, long frameStart) => SessionLogFrameCodec.DecodePlain(plain, frameStart);
}
