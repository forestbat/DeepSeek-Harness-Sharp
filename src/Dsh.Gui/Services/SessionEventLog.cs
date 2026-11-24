using Dsh.Core;
using Dsh.Persistence;
using Dsh.RemoteHost;

namespace Dsh.Gui.Services;

/**
 * 整段历史的列表视图: **信封索引常驻, 负载按需解那一条**。
 *
 * 为什么要它: 百万条事件的会话在打开时会被整段物化(实测 887k 条: 解析 4.6s、常驻 900MB),
 * 而用户真正"看"到的只有视口里那几十条。日志切片自带 `FromSeq/EventCount`, 索引不必解压任何事件;
 * 段内每一条的序号/时刻/类型由**只读信封扫描**得到(打包行只读 seq0/time0/dt, 跳过 texts/args),
 * 一条负载都不建。于是:
 *
 * - 从头扫到尾 = 查表(`Count`/`Seq`/`Time` 全都现成);
 * - 跳转/滚动到某处 = 只解那一条记录所在的**一帧**;
 * - 视口里真正要渲染的几十条 = 各解自己那一条记录。
 *
 * 每一段解出来的信封缓存在段上(几十 KB), 负载解出来的那条事件挂在信封上, 由弱引用决定留多久 ——
 * 缓存大小由真实使用决定, 不设"最多几帧/几段"这类阈值。
 *
 * 列表语义与"整段物化"完全一致: `Count` 与 `this[i]` 的形状只由索引决定, 惰性只体现在
 * 负载什么时候解。契约(条数与逐条 Seq)因此不必放宽。
 */
public sealed class SessionEventLog : IReadOnlyList<SessionEvent>
{
    /** 一段历史 = 一个日志切片覆盖的连续区间。段的条数来自切片元数据, 不必解压就能算下标。 */
    private sealed class Part(long start, int count, RemoteLogSlice? slice, List<SessionEvent>? owned)
    {
        public long Start { get; set; } = start;
        public int Count { get; set; } = count;
        public RemoteLogSlice? Slice { get; set; } = slice;
        public List<SessionEvent>? Owned { get; set; } = owned;
        /** 这一段的只读信封(按序号递增); 第一次访问时扫一次, 之后就是查表。 */
        public List<SessionLogFrameCodec.Envelope>? Envelopes { get; set; }
        /** 信封位置上解出来的事件(弱引用): 渲染过的那些留着, 没人用就回收。 */
        public Dictionary<long, WeakReference<SessionEvent>>? Loaded { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly List<Part> _parts;
    private bool _materializedAll;
    /** 每一段的绝对起始下标(前缀和): 下标定位走二分, 不走线性 —— 段有几百个时线性查找会把"扫一遍"拖成秒级。 */
    private long[]? _offsets;
    private long _total;

    private SessionEventLog(List<Part> parts)
    {
        _parts = parts;
        _total = parts.Sum(part => (long)part.Count);
    }

    /** 老路径: 事件已经在手上(整段快照, 或投影缓存命中时的增量)。 */
    public static SessionEventLog FromEvents(IReadOnlyList<SessionEvent> events)
        => new(events.Count == 0
            ? []
            : [new Part(events[0].Seq, events.Count, null, [.. events])]);

    /**
     * 惰性路径: 用切片自带的条数构成索引。
     *
     * 切片之间有空档(宿主按字节预算截断、某一段取不回来)时**照样建索引**: 缺口留成段与段之间的空位,
     * 由 `Coverage` 报给上层、再用 `Fill` 补。以前这里返回 null 让调用方退回整段物化,
     * 代价是"少几百条就要多花几秒和上 GB 内存"——而缺的那段本来就该按区间补, 不该用整段重取去换。
     */
    public static SessionEventLog? TryFromSlices(IReadOnlyList<RemoteLogSlice> slices, long total)
    {
        if (slices.Count == 0)
            return null;
        var parts = new List<Part>(slices.Count);
        foreach (var slice in slices)
        {
            // 帧边界只落在行边界上, 相邻切片会多带几条: 后面的段只收还没覆盖的那部分。
            var start = Math.Max(slice.FromSeq, LastEnd(parts));
            var end = slice.FromSeq + slice.EventCount;
            if (end <= start)
                continue;
            parts.Add(new Part(start, (int)(end - start), slice, null));
            if (total > 0 && LastEnd(parts) >= total)
                break;
        }
        return parts.Count > 0 ? new SessionEventLog(parts) : null;

        static long LastEnd(List<Part> parts)
            => parts.Count == 0 ? 0 : parts[^1].Start + parts[^1].Count;
    }

    /** 已经覆盖到的序号区间(左闭右开, 按起点递增、相邻已合并); 缺口 = 这些区间之间的空位。 */
    public IReadOnlyList<SeqRange> Coverage
    {
        get
        {
            lock (_gate)
            {
                var ranges = new List<SeqRange>(_parts.Count);
                foreach (var part in _parts)
                {
                    var range = new SeqRange(part.Start, part.Start + part.Count);
                    if (ranges.Count > 0 && ranges[^1].Union(range) is { } merged)
                    {
                        ranges[^1] = merged;
                        continue;
                    }
                    ranges.Add(range);
                }
                return ranges;
            }
        }
    }

    /** 下一条尚未覆盖的序号(索引末尾); 索引不从 0 开始时它就是首段的起点。 */
    public long NextSeq
    {
        get
        {
            lock (_gate)
                return _parts.Count == 0 ? 0 : _parts[^1].Start + _parts[^1].Count;
        }
    }

    /** 这条序号在索引里吗(补洞用; 已覆盖的不重复收)。 */
    public bool Covers(long seq)
    {
        lock (_gate)
            return FindPart(seq) is not null;
    }

    /**
     * 补洞: 把按区间取回的事件插到索引里(位置可以不在末尾), 已覆盖的序号跳过。
     *
     * 段按起点递增排列, 因此插入后仍可二分定位; 补回来的这一段照常惰性无关(直接持有事件)。
     */
    public void Fill(IReadOnlyList<SessionEvent> events)
    {
        if (events.Count == 0)
            return;
        lock (_gate)
        {
            List<SessionEvent>? run = null;
            foreach (var sessionEvent in events.OrderBy(item => item.Seq))
            {
                if (FindPart(sessionEvent.Seq) is not null)
                {
                    FlushRun();
                    continue;
                }
                if (run is { Count: > 0 } && sessionEvent.Seq != run[^1].Seq + 1)
                    FlushRun();
                (run ??= []).Add(sessionEvent);
            }
            FlushRun();
            Invalidate();

            void FlushRun()
            {
                if (run is not { Count: > 0 })
                    return;
                Insert(new Part(run[0].Seq, run.Count, null, run));
                run = null;
            }
        }

        void Insert(Part part)
        {
            var index = _parts.Count;
            for (var candidate = 0; candidate < _parts.Count; candidate += 1)
            {
                if (_parts[candidate].Start > part.Start)
                {
                    index = candidate;
                    break;
                }
            }
            _parts.Insert(index, part);
        }
    }

    /** 包含该序号的段; 没有就是缺口。 */
    private Part? FindPart(long seq)
    {
        var low = 0;
        var high = _parts.Count - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var part = _parts[middle];
            if (seq < part.Start)
            {
                high = middle - 1;
                continue;
            }
            if (seq >= part.Start + part.Count)
            {
                low = middle + 1;
                continue;
            }
            return part;
        }
        return null;
    }

    /** 惰性视图为 true: 还没有为"取某一条"而把整段解出来过。 */
    public bool IsPaged => !_materializedAll && _parts.Exists(part => part.Slice is not null);

    /** 索引里剩下的事件总数(含实时追加, 不含已释放的前缀)。 */
    public long IndexedEvents
    {
        get
        {
            lock (_gate)
                return TotalLocked();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return (int)TotalLocked();
        }
    }

    /**
     * 第 i 条: 序号/时刻/类型来自信封(查表), 负载留到真正访问 `Data` 时才解那一条记录。
     *
     * "从头扫到尾"因此不付任何解码代价; 视口里那几十条各解自己那一条。
     */
    public SessionEvent this[int index]
    {
        get
        {
            lock (_gate)
            {
                var (part, offset) = Locate(index);
                if (part.Owned is { } owned)
                    return owned[offset];
                var envelopes = EnvelopesOf(part);
                if (offset >= envelopes.Count)
                    throw new SessionPageUnavailableException(
                        $"段 #{part.Start} 只有 {envelopes.Count} 条信封, 取不到第 {offset} 条");
                return EnvelopeEvent(part, envelopes[offset]);
            }
        }
    }

    /** 从 [index, index + count) 取一份**带负载**的物化副本: 折叠(投影)要读负载, 一次解一批。 */
    public IReadOnlyList<SessionEvent> Slice(int index, int count)
    {
        lock (_gate)
        {
            var available = (int)TotalLocked();
            var wanted = Math.Min(count, available - index);
            var batch = new List<SessionEvent>(Math.Max(wanted, 0));
            while (batch.Count < wanted)
            {
                var (part, offset) = Locate(index + batch.Count);
                var events = part.Owned ?? DecodePart(part);
                var take = Math.Min(wanted - batch.Count, events.Count - offset);
                if (take <= 0)
                    throw new SessionPageUnavailableException(
                        $"段 #{part.Start}({part.Count} 条) 解出来只有 {events.Count} 条, 取不到第 {index + batch.Count} 条");
                batch.AddRange(events.GetRange(offset, take));
            }
            return batch;
        }
    }

    /** 实时追加: 与末段连续且已物化就并进去, 否则另起一段(截断后重写会走到这里)。 */
    public void Append(IReadOnlyList<SessionEvent> events)
    {
        lock (_gate)
        {
            foreach (var sessionEvent in events)
            {
                if (_parts.Count > 0 && _parts[^1] is { Slice: null } tail && tail.Owned is { } owned
                    && sessionEvent.Seq == tail.Start + tail.Count)
                {
                    owned.Add(sessionEvent);
                    tail.Count += 1;
                    continue;
                }
                _parts.Add(new Part(sessionEvent.Seq, 1, null, [sessionEvent]));
            }
            Invalidate();
        }
    }

    /**
     * 投影消费完的前缀: 放掉的条数从第一段头上扣掉, 段少了一段就整段删。
     *
     * 段头只放掉一部分时必须**就地挪起点**: 段内偏移与绝对位置靠 `Part.Start` 对齐,
     * 否则 `this[i]` 会退回到已经放掉的那几条上(表现为实时事件全被当成"已渲染"而丢掉)。
     */
    public void RemovePrefix(int count)
    {
        lock (_gate)
        {
            var left = count;
            while (left > 0 && _parts.Count > 0)
            {
                var head = _parts[0];
                if (left >= head.Count)
                {
                    left -= head.Count;
                    _parts.RemoveAt(0);
                    continue;
                }
                if (head.Owned is { } owned)
                    owned.RemoveRange(0, left);
                head.Envelopes = null;
                head.Loaded = null;
                head.Start += left;
                head.Count -= left;
                left = 0;
            }
            Invalidate();
        }
    }

    /** 就地截断(撤回): 丢掉序号 >= keepEventCount 的事件。 */
    public void TrimFrom(long keepEventCount)
    {
        lock (_gate)
        {
            while (_parts.Count > 0)
            {
                var last = _parts[^1];
                if (last.Start >= keepEventCount)
                {
                    _parts.RemoveAt(_parts.Count - 1);
                    continue;
                }
                var keepCount = (int)Math.Min(last.Count, keepEventCount - last.Start);
                if (keepCount < last.Count)
                {
                    if (last.Owned is { } owned)
                        owned.RemoveRange(keepCount, owned.Count - keepCount);
                    last.Envelopes = null;
                    last.Loaded = null;
                    last.Count = keepCount;
                }
                break;
            }
            Invalidate();
        }
    }

    /** 这一段的只读信封(第一次访问时扫一次)。扫的是明文, 但一条负载都不建。 */
    private List<SessionLogFrameCodec.Envelope> EnvelopesOf(Part part)
    {
        if (part.Envelopes is { } cached)
            return cached;
        try
        {
            var envelopes = SessionLogFrameCodec.ReadEnvelopes(part.Slice!.Bytes, part.Start, part.Start + part.Count);
            if (envelopes.Count != part.Count)
                throw new FormatException($"段 #{part.Start} 应有 {part.Count} 条, 信封扫出 {envelopes.Count} 条");
            part.Envelopes = envelopes;
            return envelopes;
        }
        catch (Exception error) when (IsUnreadable(error))
        {
            // 索引与字节对不上: 先整体退回物化(与改动前同一条路径), 再按事件取。
            MaterializeAll();
            throw new SessionPageUnavailableException(
                $"段 #{part.Start}({part.Count} 条)的信封扫不出来, 已退回整段物化: {error.Message}", error);
        }
    }

    /** 让一条信封变成事件: 序号/时刻/类型现成, 负载等真正访问时解那一条记录。 */
    private SessionEvent EnvelopeEvent(Part part, SessionLogFrameCodec.Envelope envelope)
    {
        part.Loaded ??= [];
        if (part.Loaded.TryGetValue(envelope.Seq, out var cached) && cached.TryGetTarget(out var alive))
            return alive;
        var sessionEvent = SessionEvent.Deferred(envelope.Seq, () => Load(part, envelope));
        part.Loaded[envelope.Seq] = new WeakReference<SessionEvent>(sessionEvent);
        return sessionEvent;
    }

    /** 解出信封指向的那一条记录(打包行会带出整块成员), 找出这一条。 */
    private SessionEvent Load(Part part, SessionLogFrameCodec.Envelope envelope)
    {
        try
        {
            foreach (var sessionEvent in SessionLogFrameCodec.DecodeRecord(part.Slice!.Bytes, envelope))
            {
                if (sessionEvent.Seq == envelope.Seq)
                    return sessionEvent;
            }
            throw new FormatException($"记录里没有 #{envelope.Seq}");
        }
        catch (Exception error) when (IsUnreadable(error))
        {
            MaterializeAll();
            var offset = (int)(envelope.Seq - part.Start);
            var owned = part.Owned ?? throw new SessionPageUnavailableException(
                $"段 #{part.Start} 的第 #{envelope.Seq} 条解不出来, 整段物化也没能修好: {error.Message}", error);
            return owned[offset];
        }
    }

    /** 整段解码(折叠要读负载): 每段解一次, 结果留着供 `Slice` 与按需负载共用。 */
    private List<SessionEvent> DecodePart(Part part)
    {
        if (part.Owned is { } owned)
            return owned;
        var slice = part.Slice ?? throw new InvalidOperationException($"段 #{part.Start} 既没有切片也没有事件");
        // 直接按批收进结果(一趟): 先把整段拼成一个列表再筛一遍, 在百万条的会话上等于多搬一道内存。
        var decoded = new List<SessionEvent>(part.Count);
        try
        {
            foreach (var batch in SessionLogSliceParser.ParseBatches(slice.Bytes, slice.FromSeq))
            {
                foreach (var sessionEvent in batch)
                {
                    if (sessionEvent.Seq < part.Start || sessionEvent.Seq >= part.Start + part.Count)
                        continue;
                    decoded.Add(sessionEvent);
                }
            }
            if (decoded.Count != part.Count)
                throw new FormatException($"段 #{part.Start} 应有 {part.Count} 条, 解出 {decoded.Count} 条");
        }
        catch (Exception error) when (IsUnreadable(error))
        {
            try
            {
                MaterializeAll();
            }
            catch (Exception fatal) when (IsUnreadable(fatal))
            {
                throw new SessionPageUnavailableException(
                    $"段 #{part.Start}({part.Count} 条)解不出来, 整段物化也没能修好: {fatal.Message}", fatal);
            }
            return part.Owned!;
        }
        part.Owned = decoded;
        return decoded;
    }

    private static bool IsUnreadable(Exception error)
        => error is FormatException or InvalidOperationException or IOException;

    /** 兜底: 把每一段都解出来, 之后按老路径(整段物化)继续。 */
    private void MaterializeAll()
    {
        if (_materializedAll)
            return;
        _materializedAll = true;
        foreach (var part in _parts)
        {
            if (part.Owned is not null)
                continue;
            var decoded = new List<SessionEvent>(part.Count);
            foreach (var batch in SessionLogSliceParser.ParseBatches(part.Slice!.Bytes, part.Slice.FromSeq))
            {
                foreach (var sessionEvent in batch)
                {
                    if (sessionEvent.Seq < part.Start || sessionEvent.Seq >= part.Start + part.Count)
                        continue;
                    decoded.Add(sessionEvent);
                }
            }
            if (decoded.Count != part.Count)
                throw new SessionPageUnavailableException(
                    $"段 #{part.Start} 应有 {part.Count} 条, 整段物化得到 {decoded.Count} 条");
            part.Owned = decoded;
            part.Slice = null;
            part.Envelopes = null;
            part.Loaded = null;
        }
    }

    /** 绝对位置 -> (段, 段内偏移): 前缀和 + 二分。 */
    private (Part Part, int Offset) Locate(int absolute)
    {
        if (absolute < 0)
            throw new ArgumentOutOfRangeException(nameof(absolute), "会话历史的下标不能是负数");
        var offsets = _offsets ??= BuildOffsets();
        var low = 0;
        var high = offsets.Length - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (absolute < offsets[middle])
            {
                high = middle - 1;
                continue;
            }
            if (middle + 1 < offsets.Length && absolute >= offsets[middle + 1])
            {
                low = middle + 1;
                continue;
            }
            return (_parts[middle], (int)(absolute - offsets[middle]));
        }
        throw new ArgumentOutOfRangeException(nameof(absolute), $"下标 {absolute} 超出会话历史({Count} 条)");
    }

    /** 结构变了(追加/释放前缀/截断): 前缀和与总数一起重算 —— 总数不能靠定位时顺手刷新, 它被 Count 直接读。 */
    private void Invalidate()
    {
        _offsets = null;
        var total = 0L;
        foreach (var part in _parts)
            total += part.Count;
        _total = total;
    }

    /** 每段的绝对起始下标(= 段内偏移的基准); 任何结构变化(追加/释放前缀/截断)都作废缓存。 */
    private long[] BuildOffsets()
    {
        var offsets = new long[_parts.Count];
        var running = 0L;
        for (var index = 0; index < _parts.Count; index += 1)
        {
            offsets[index] = running;
            running += _parts[index].Count;
        }
        return offsets;
    }

    private long TotalLocked() => _total;

    public IEnumerator<SessionEvent> GetEnumerator()
    {
        for (var index = 0; index < Count; index += 1)
            yield return this[index];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/** 一段历史解不出来(日志本身不可读): 调用方据此报给用户, 而不是显示半段历史。 */
public sealed class SessionPageUnavailableException(string message, Exception? inner = null) : Exception(message, inner);