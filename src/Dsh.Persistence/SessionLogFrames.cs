using Dsh.Core;

namespace Dsh.Persistence;

/**
 * 一份会话日志的**帧视图**: 头帧 + 按需产出的帧序列(惰性, 一次只在内存里留一帧)。
 *
 * 这是两个后端共用的交换格式 —— "日志 = 头帧 + 一串 zstd 帧"既是 `session.jsonl.zstd` 的字节布局,
 * 也是库里 `log.header_frame` + `frame.bytes` 的布局。于是切片、导出、导入、交接都写在这一个类型上:
 * 每个后端只提供"按事件区间产出帧"的一件事实(`ReadFrames`), 其余逻辑不再各写一遍。
 */
public sealed record SessionLogFrames(
    SessionHeader Header,
    long InheritedEventCount,
    byte[] HeaderFrame,
    long TotalEventCount,
    Func<long, int, IEnumerable<SessionLogFrames.Frame>> ReadFrames)
{
    /** 一帧: 首序号、事件条数、原样压缩字节(不解压、不重编码), 以及帧内 replace 的最小起点。 */
    public readonly record struct Frame(long FirstSeq, int EventCount, byte[] Bytes)
    {
        public long MinReplaceStart { get; init; } = long.MaxValue;
    }

    /** `maxEvents <= 0` 表示"从这里到末尾"。 */
    public IEnumerable<Frame> All => ReadFrames(0, 0);

    /**
     * 覆盖 `[fromEvent, fromEvent + maxEvents)` 的那一段日志字节: 头帧 + 命中帧, 一个字节都不解压。
     *
     * 与 `ISessionPersistence.TryReadLogSlice` 的语义一致: `FromSeq` 是**覆盖请求起点的那一帧**的首序号,
     * 可能早于请求的起点(帧边界只落在行边界上, 不许劈行), 接收方按序号裁剪。
     * 没有帧(空日志/取不到)返回 null, 调用方退回事件快照。
     */
    public SessionLogSlice? ToSlice(long fromEvent, int maxEvents)
    {
        if (TotalEventCount <= 0)
            return null;
        var start = Math.Clamp(fromEvent, 0, TotalEventCount);
        var count = maxEvents > 0 ? (int)Math.Min(maxEvents, TotalEventCount - start) : (int)(TotalEventCount - start);
        if (count <= 0)
            return null;
        var frames = ReadFrames(start, count).ToList();
        if (frames.Count == 0)
            return null;
        var total = HeaderFrame.Length + frames.Sum(frame => (long)frame.Bytes.Length);
        if (total > int.MaxValue)
            throw new IOException($"session log window is larger than addressable memory: {total} bytes");
        var bytes = new byte[total];
        HeaderFrame.CopyTo(bytes, 0);
        var offset = HeaderFrame.Length;
        foreach (var frame in frames)
        {
            frame.Bytes.CopyTo(bytes, offset);
            offset += frame.Bytes.Length;
        }
        return new SessionLogSlice(bytes, Header, frames[0].FirstSeq, frames.Sum(frame => frame.EventCount),
            TotalEventCount, SessionLogSlice.HashOf(bytes));
    }

    /** 整份日志按原样写出(导出成 `session.jsonl.zstd` / 交给别的中心): 头帧 + 全部帧。 */
    public void WriteTo(Stream target)
    {
        target.Write(HeaderFrame);
        foreach (var frame in All)
            target.Write(frame.Bytes);
    }

    /** 由事件现编一份帧视图(明文日志、或需要重新切帧时): 切帧规则与两个后端的写路径共用。 */
    public static SessionLogFrames FromEvents(
        SessionHeader header, long inheritedEventCount, IReadOnlyList<SessionEvent> events, int plaintextBudget, bool packChunks)
    {
        var headerFrame = ZstdFrames.CompressFrame(
            System.Text.Encoding.UTF8.GetBytes(SessionLogHeader.WriteHeaderLine(header, header.IsSeeded ? inheritedEventCount : null) + "\n"));
        var frames = SessionFrameWriter.Encode(events, packChunks, plaintextBudget, compress: true)
            .Select(frame => new Frame(frame.FirstSeq, frame.EventCount, frame.Content)
            {
                MinReplaceStart = frame.MinReplaceStart,
            })
            .ToList();
        return new SessionLogFrames(header, inheritedEventCount, headerFrame, events.Count, Covering(frames));
    }

    /** 内存里那几帧按区间挑选: 与两个后端的按区间查询同一语义(帧覆盖到区间就给整帧)。 */
    public static Func<long, int, IEnumerable<Frame>> Covering(IReadOnlyList<Frame> frames) => (fromEvent, maxEvents) =>
    {
        if (frames.Count == 0)
            return [];
        var start = Math.Clamp(fromEvent, 0, frames[^1].FirstSeq + frames[^1].EventCount);
        var end = maxEvents > 0 ? start + maxEvents : long.MaxValue;
        return frames.Where(frame => frame.FirstSeq < end && frame.FirstSeq + frame.EventCount > start);
    };
}

