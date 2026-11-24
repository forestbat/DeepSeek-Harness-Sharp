using System.Runtime.InteropServices;
using Dsh.Core;

namespace Dsh.Persistence;

internal sealed record SessionLogScan(
    SessionHeader Meta,
    long InheritedEventCount,
    List<SessionEvent> Events,
    long CommittedBytes,
    LogDamage Damage);

/**
 * 逐行解析会话日志的已提交区。
 *
 * 容错是硬要求: 一条记录读不出来(跨版本 codec 漂移、半行、被外部工具改坏)不能让整份会话打不开 ——
 * 打开一个降级的会话比打不开更有价值。所以这里把读不出来的记录记成 LogDamage 并跳过, 绝不抛异常;
 * 只要出现过损伤, 整个事件的 seq 就不再可信, 收尾时统重编号成 0..n-1(Session 要求 seq 从 0 连续)。
 */
internal sealed class SessionLogScanner
{
    private const byte Newline = (byte)'\n';
    private readonly List<byte> _fragment = [];
    private readonly long _seqBase;
    private List<SessionEvent> _events = [];
    private bool _finished;
    private LogDamage _damage = LogDamage.None;
    /** 到目前为止收下的事件总数(含已经取走的部分): 序号连续性与落后重放的判据都靠它, 不是 _events.Count。 */
    private long _taken;
    /** 已经被取走的事件数(见 TakeEvents): 收尾重编号要按它给出绝对下标。 */
    private long _released;

    /**
     * seqBase: 扫描起点对应的绝对 seq。缺页重读拿到的是一段中间帧, 它本来就从中途开始,
     * 不能按"seq 必须从 0 开始"判损伤, 也不能在损伤时重编号(窗口要按绝对 seq 过滤)。
     */
    public SessionLogScanner(ReadOnlySpan<byte> headerRecord, long seqBase = 0)
    {
        _seqBase = seqBase;
        var parsed = SessionLogHeader.ParseHeaderRecord(headerRecord);
        Meta = parsed.Meta;
        InheritedEventCount = parsed.InheritedEventCount;
        InputBytes = headerRecord.Length;
        CommittedBytes = headerRecord.Length;
    }

    public SessionHeader Meta { get; }
    public long InheritedEventCount { get; private set; }
    public long InputBytes { get; private set; }

    /** 最后一个完整、可解析记录的结束位置; 只在没有损伤时用作"尾部半帧"的截断点。 */
    public long CommittedBytes { get; private set; }

    public LogDamage Damage => _damage;

    public void Write(ReadOnlySpan<byte> chunk)
    {
        if (_finished) throw new InvalidOperationException("cannot write to a finished session log scanner");
        var chunkStart = InputBytes;
        InputBytes += chunk.Length;
        var lineStart = 0;
        while (true)
        {
            var newline = chunk[lineStart..].IndexOf(Newline);
            if (newline < 0) break;
            var newlineIndex = lineStart + newline;
            var endByte = chunkStart + newlineIndex + 1;
            if (_fragment.Count == 0)
            {
                ConsumeEventLine(chunk[lineStart..newlineIndex], endByte);
            }
            else
            {
                _fragment.AddRange(chunk[lineStart..newlineIndex]);
                ConsumeEventLine(CollectionsMarshal.AsSpan(_fragment), endByte);
                _fragment.Clear();
            }
            lineStart = newlineIndex + 1;
        }
        if (lineStart < chunk.Length) _fragment.AddRange(chunk[lineStart..]);
    }

    public (long InputBytes, long CommittedBytes, long EventCount) Checkpoint()
        => (InputBytes, CommittedBytes, _taken);

    /**
     * 取走到目前为止收下的事件, 扫描器随后从头攒下一批。
     *
     * 逐帧解析用: 一帧解出来就交给调用方(投影)消费, 消费完这一批就能被回收,
     * 于是同一时刻常驻的已解析事件只有一批, 而不是整段日志那么多。
     * 序号连续性与损伤判定不受影响 —— 它们看的是 _taken(累计收下多少), 不是手上还剩多少。
     */
    public IReadOnlyList<SessionEvent> TakeEvents()
    {
        if (_events.Count == 0)
            return [];
        var taken = _events;
        _released += taken.Count;
        _events = [];
        return taken;
    }

    public SessionLogScan Finish()
    {
        _finished = true;
        if (_fragment.Count > 0)
        {
            // 尾部半行: 断电或被杀时写了一半的记录。
            _damage = _damage.Plus(1, InputBytes - _fragment.Count, CommittedBytes);
            _fragment.Clear();
        }
        if (_damage.Any && _seqBase == 0) Normalize();
        return new SessionLogScan(Meta, InheritedEventCount, _events, CommittedBytes, _damage);
    }

    private void ConsumeEventLine(ReadOnlySpan<byte> line, long endByte)
    {
        IReadOnlyList<SessionEvent> decoded;
        try
        {
            decoded = DecodeRecord(line);
        }
        catch (Exception)
        {
            // 读不出来就是损伤, 不是"整份日志有毒"; 记下位置继续读后面的记录。
            _damage = _damage.Plus(1, endByte - line.Length - 1, CommittedBytes);
            return;
        }
        if (_damage.Any)
        {
            // 已经知道 seq 不可信了, 直接收下, 收尾统一重编号。
            Accept(decoded);
            return;
        }
        if (IsStaleReplay(decoded))
        {
            CommittedBytes = endByte;
            return;
        }
        var rowStart = _events.Count;
        foreach (var sessionEvent in decoded)
        {
            if (sessionEvent.Seq != _seqBase + _taken)
            {
                // 序号断裂同样只是损伤: 丢掉这一行刚加的部分, 记为损伤, 交给收尾重编号。
                _taken -= _events.Count - rowStart;
                _events.RemoveRange(rowStart, _events.Count - rowStart);
                _damage = _damage.Plus(1, endByte - line.Length - 1, CommittedBytes);
                Accept(decoded);
                return;
            }
            _events.Add(sessionEvent);
            _taken += 1;
        }
        CommittedBytes = endByte;
    }

    private void Accept(IReadOnlyList<SessionEvent> decoded)
    {
        _events.AddRange(decoded);
        _taken += decoded.Count;
    }

    /**
     * 损伤之后的重编号: 事件必须 0..n-1 连续(Session 的构造契约), 同时把 seed 切点夹到事件数以内,
     * 否则 Session 会因为"继承事件数超过日志长度"拒绝构造。
     */
    private void Normalize()
    {
        for (var index = 0; index < _events.Count; index += 1)
        {
            if (_events[index].Seq != _released + index)
                _events[index] = _events[index] with { Seq = _released + index };
        }
        if (InheritedEventCount > _taken)
            InheritedEventCount = _taken;
    }

    /**
     * 落后的重放块: 另一个写入者带着落后于已提交区间的序号, 把同一段历史又追加了一遍。
     * 整块都落在已提交区间内才认定为重放并整体丢弃; 与已提交区间交错则是损伤, 交给上面的判定。
     */
    private bool IsStaleReplay(IReadOnlyList<SessionEvent> decoded)
        => decoded[0].Seq < _seqBase + _taken && decoded[^1].Seq < _seqBase + _taken;

    /**
     * 直接按字节解码: 普通事件不需要 DOM, 只有 chunk 打包行与带区间来源的记录才退回 JsonNode。
     * 旧实现对每条记录都先 JsonNode.Parse 建树、再序列化回喂给反序列化, 等于每条记录多解析一遍。
     */
    private static IReadOnlyList<SessionEvent> DecodeRecord(ReadOnlySpan<byte> line)
        => ChunkRows.DecodeStorageRecord(line);
}

internal static class SessionLogScannerCompat
{
    public static SessionLogScan ScanLog(byte[] buffer)
    {
        var headerEnd = Array.IndexOf(buffer, (byte)'\n');
        if (headerEnd < 0) throw new FormatException("empty or header-less session log");
        var scanner = new SessionLogScanner(buffer.AsSpan(0, headerEnd + 1));
        scanner.Write(buffer.AsSpan(headerEnd + 1));
        return scanner.Finish();
    }
}