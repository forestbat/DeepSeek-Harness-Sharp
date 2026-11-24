using Dsh.Core;

namespace Dsh.Persistence;

/**
 * 日志切片里**单帧**的读取: 切帧只读帧头(不解压), 只有命中的那一帧才解压、才建事件。
 *
 * 为什么要单帧粒度: 客户端按页惰性持有切片, 而"跳到某条消息"通常落在页中间。
 * 解整页要几十毫秒, 解一帧只要几毫秒 —— 用户拖滚动条、来回跳转时差的正是这一截。
 */
public static class SessionLogFrameCodec
{
    /** 一条事件的**只读信封**: 序号/时刻/类型立刻可得, 负载要按位置去解那一条记录。 */
    public readonly record struct Envelope(long Seq, long Time, string Type, int Frame, int LineStart, int LineLength);

    /**
     * 把整段切片读成信封: 逐帧解压, 但**一条负载都不建**。
     *
     * "从头扫到尾"只需要这一步(查表), 真正要渲染的那几条才用 `DecodeRecord` 解出来。
     */
    public static List<Envelope> ReadEnvelopes(byte[] sliceBytes, long fromSeq, long toSeqExclusive)
    {
        ArgumentNullException.ThrowIfNull(sliceBytes);
        var frames = EventFrames(sliceBytes);
        var envelopes = new List<Envelope>();
        var raw = new List<ChunkRows.StoredEnvelope>();
        for (var index = 0; index < frames.Count; index += 1)
        {
            raw.Clear();
            var frame = frames[index];
            var plain = ZstdFrames.DecompressFrame(sliceBytes, frame.Start, frame.End - frame.Start, frame.Start);
            ChunkRows.ReadEnvelopes(plain, raw);
            foreach (var envelope in raw)
            {
                if (envelope.Seq < fromSeq || envelope.Seq >= toSeqExclusive)
                    continue;
                envelopes.Add(new Envelope(envelope.Seq, envelope.Time, envelope.Type, index,
                    envelope.LineStart, envelope.LineLength));
            }
        }
        envelopes.Sort((left, right) => left.Seq.CompareTo(right.Seq));
        return envelopes;
    }

    /** 按信封的位置解出**那一条**记录的事件(打包行会一次给出它的整块成员)。 */
    public static IReadOnlyList<SessionEvent> DecodeRecord(byte[] sliceBytes, Envelope envelope)
    {
        var frames = EventFrames(sliceBytes);
        if (envelope.Frame < 0 || envelope.Frame >= frames.Count)
            throw new FormatException($"envelope #{envelope.Seq} points at frame {envelope.Frame} of {frames.Count}");
        var frame = frames[envelope.Frame];
        var plain = ZstdFrames.DecompressFrame(sliceBytes, frame.Start, frame.End - frame.Start, frame.Start);
        if (envelope.LineStart < 0 || envelope.LineStart + envelope.LineLength > plain.Length)
            throw new FormatException($"envelope #{envelope.Seq} points outside the frame at byte {frame.Start}");
        var decoded = ChunkRows.DecodeStorageRecordLazy(plain.AsMemory(envelope.LineStart, envelope.LineLength));
        foreach (var sessionEvent in decoded)
        {
            if (sessionEvent.Seq == envelope.Seq)
                return [sessionEvent];
        }
        return decoded;
    }
    /** 头帧之后的每一帧的字节区间; 结构有疑点(坏帧/半帧)时抛 FormatException。 */
    public static IReadOnlyList<(int Start, int End)> EventFrames(byte[] sliceBytes)
    {
        ArgumentNullException.ThrowIfNull(sliceBytes);
        var scan = ZstdFrames.Scan(sliceBytes);
        if (scan.Frames.Count == 0 || scan.Damaged.Count > 0 || scan.TornStart is not null)
            throw new FormatException("session log slice is corrupt");
        var frames = new List<(int Start, int End)>(Math.Max(0, scan.Frames.Count - 1));
        // 第 0 帧是头行, 不含事件。
        for (var slot = 1; slot < scan.Frames.Count; slot += 1)
            frames.Add((scan.Frames[slot].Start, scan.Frames[slot].End));
        return frames;
    }

    /** 这一帧有多少条事件、首序号是多少: 解压后只扫记录头, 不建事件对象。 */
    public static (long FirstSeq, int EventCount) Measure(byte[] sliceBytes, (int Start, int End) frame)
    {
        var plain = ZstdFrames.DecompressFrame(sliceBytes, frame.Start, frame.End - frame.Start, frame.Start);
        var (firstSeq, count) = ChunkRows.MeasureStorageRecords(plain);
        if (count == 0)
            throw new FormatException($"session log frame at byte {frame.Start} holds no events");
        return (firstSeq, count);
    }

    /**
     * 解出这一帧里 `[fromSeq, fromSeq + count)` 这一段事件。
     *
     * 段边界落在帧中间是常态(相邻切片按帧对齐, 会多带前一段的尾巴), 所以这里按序号裁剪,
     * 并要求裁出来的这一段序号连续 —— 损伤不该被悄悄吞掉。
     */
    public static List<SessionEvent> Decode(byte[] sliceBytes, (int Start, int End) frame, long fromSeq, int count)
    {
        var plain = ZstdFrames.DecompressFrame(sliceBytes, frame.Start, frame.End - frame.Start, frame.Start);
        var decoded = DecodePlain(plain, frame.Start);
        var end = fromSeq + count;
        var events = new List<SessionEvent>(count);
        foreach (var sessionEvent in decoded)
        {
            if (sessionEvent.Seq < fromSeq || sessionEvent.Seq >= end)
                continue;
            if (events.Count > 0 && sessionEvent.Seq != events[^1].Seq + 1)
                throw new FormatException(
                    $"session log frame at byte {frame.Start} is not contiguous ({events[^1].Seq} -> {sessionEvent.Seq})");
            events.Add(sessionEvent);
        }
        if (events.Count == 0)
            throw new FormatException($"session log frame at byte {frame.Start} holds no event in [#{fromSeq}, #{end})");
        if (events[0].Seq != fromSeq)
            throw new FormatException(
                $"session log frame at byte {frame.Start} starts at #{events[0].Seq}, expected #{fromSeq}");
        return events;
    }

    /**
     * 逐行解码一段明文(窗口读与按帧读共用这一份)。解不出来的行一律抛, 让调用方退回全量解析
     * (那里有损伤记录与修复)。
     *
     * 序号落后的**整块重放**不抛: 另一个写入者带着更早的游标把同一段历史又追加了一遍,
     * 全量解析(SessionLogScanner.IsStaleReplay)也是整块丢弃这些行的。这里沿用同一条判据,
     * 否则一份"被两个写入者交错写过"的日志会让每一页都读不回来。
     * 只有"前一半已提交、后一半还在前面"这种交错形状才当损伤抛出去。
     */
    internal static List<SessionEvent> DecodePlain(byte[] plain, long frameStart)
    {
        var events = new List<SessionEvent>();
        long? next = null;
        var lineStart = 0;
        while (lineStart < plain.Length)
        {
            var newline = Array.IndexOf(plain, (byte)'\n', lineStart);
            if (newline < 0)
                break;
            var line = plain.AsMemory(lineStart, newline - lineStart);
            lineStart = newline + 1;
            if (line.IsEmpty)
                continue;
            IReadOnlyList<SessionEvent> decoded;
            try
            {
                decoded = ChunkRows.DecodeStorageRecordLazy(line);
            }
            catch (Exception error) when (error is not SessionFormatUnsupportedException)
            {
                throw new FormatException(
                    $"session log frame at byte {frameStart} has an unreadable record: {error.Message}", error);
            }
            if (decoded.Count == 0)
                continue;
            var firstSeq = decoded[0].Seq;
            var lastSeq = decoded[^1].Seq;
            if (next is { } expected)
            {
                if (lastSeq < expected)
                    continue;
                if (firstSeq != expected)
                    throw new FormatException(
                        $"session log range at byte {frameStart} has out-of-order seq ({expected} -> {firstSeq})");
            }
            events.AddRange(decoded);
            next = lastSeq + 1;
        }
        return events;
    }
}