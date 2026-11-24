using System.Text;
using Dsh.Core;

namespace Dsh.Persistence;

/**
 * 把一段日志字节解析回事件: 与读整份日志走同一套解压与行解析(SessionLogScanner), 不引入第二种解析。
 *
 * 切片的起点不是 0 时必须带上绝对序号(seqBase), 否则解出来的事件会按 0 起编号, 与实时流接不上。
 */
public static class SessionLogSliceParser
{
    /**
     * 解析切片。任何一帧解不出来就抛 FormatException: 让调用方退回"整段取快照"的旧路径,
     * 而不是拿半段历史去投影(半段历史正是滚动条与时间轴残缺的来源)。
     */
    public static IReadOnlyList<SessionEvent> Parse(byte[] bytes, long seqBase)
    {
        var events = new List<SessionEvent>();
        foreach (var batch in ParseBatches(bytes, seqBase))
            events.AddRange(batch);
        return events;
    }

    /**
     * 逐帧解析: 解出一帧就交出一批, 交出去之后扫描器不再持有它。
     *
     * 调用方边收边消费(投影), 于是同一时刻常驻的已解析事件只有一批(最大一帧的明文量),
     * 而不是整段切片的那么多。与 Parse 解出的事件、顺序、抛错条件完全一致 —— 两者共用同一条解析路径。
     *
     * 帧结构先整段验明(损坏/半帧当场抛), 记录级损伤在收尾抛: 那时调用方可能已经消费了几批,
     * 所以只有"整段事件都要么全用、要么全不用"的调用方才能靠 Parse 一次拿全。
     */
    public static IEnumerable<IReadOnlyList<SessionEvent>> ParseBatches(byte[] bytes, long seqBase)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var scan = ZstdFrames.Scan(bytes);
        if (scan.Frames.Count == 0 || scan.Damaged.Count > 0 || scan.TornStart is not null)
            throw new FormatException("session log slice is corrupt");
        var header = scan.Frames[0];
        var headerPlaintext = ZstdFrames.DecompressFrame(bytes, header.Start, header.End - header.Start, header.Start);
        if (headerPlaintext.Length == 0 || Array.IndexOf(headerPlaintext, (byte)'\n') != headerPlaintext.Length - 1)
            throw new FormatException("session log slice does not start with exactly one header frame");
        var scanner = new SessionLogScanner(headerPlaintext, seqBase);
        for (var slot = 1; slot < scan.Frames.Count; slot += 1)
        {
            var frame = scan.Frames[slot];
            scanner.Write(ZstdFrames.DecompressFrame(bytes, frame.Start, frame.End - frame.Start, frame.Start));
            var batch = scanner.TakeEvents();
            if (batch.Count > 0)
                yield return batch;
        }
        var parsed = scanner.Finish();
        if (parsed.Damage.Any)
            throw new FormatException($"{parsed.Damage.Records} unreadable record(s) in session log slice");
        if (parsed.Events.Count > 0)
            yield return parsed.Events;
    }

    /** 切片头帧里的会话头: 只解头帧, 不碰事件帧。 */
    public static SessionHeader ReadHeader(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var scan = ZstdFrames.Scan(bytes, maxFrames: 1);
        if (scan.Frames.Count == 0)
            throw new FormatException("session log slice has no header frame");
        var frame = scan.Frames[0];
        var plaintext = ZstdFrames.DecompressFrame(bytes, frame.Start, frame.End - frame.Start, frame.Start);
        var line = Encoding.UTF8.GetString(plaintext, 0, plaintext.Length - 1);
        return SessionLogHeader.ParseHeader(line)?.Meta
            ?? throw new FormatException("session log slice header is not a session header");
    }
}