using System.Text;

namespace Dsh.Persistence.Database;

/** `log` 行的头帧编解码: 与 JSONL 后端第一帧完全同构(同一段 JSON 行、同样压缩)。 */
internal static class SessionRowCodec
{
    public static byte[] EncodeHeaderFrame(Dsh.Core.SessionHeader header, long inheritedEventCount)
        => ZstdFrames.CompressFrame(Encoding.UTF8.GetBytes(
            SessionLogHeader.WriteHeaderLine(header, header.IsSeeded ? inheritedEventCount : null) + "\n"));

    public static SessionStorageMetadata DecodeHeaderFrame(byte[] frame)
        => SessionLogHeader.ParseHeaderRecord(ZstdFrames.DecompressFrame(frame, 0));

    /** 一行帧解成事件; 帧内事件必须连续, 否则当成损坏抛出去。 */
    public static IReadOnlyList<Dsh.Core.SessionEvent> DecodeFrames(
        IEnumerable<(long FirstSeq, int EventCount, byte[] Bytes)> frames, long fromSeq, long toSeqExclusive)
    {
        var events = new List<Dsh.Core.SessionEvent>();
        foreach (var (firstSeq, eventCount, bytes) in frames)
        {
            var decoded = SessionLogFrameCodec.DecodeFrame(bytes, firstSeq, eventCount);
            AppendInRange(decoded, fromSeq, toSeqExclusive, events);
        }
        return events;
    }

    /** 把解出来的一帧按区间收进结果(帧覆盖到区间就给整帧, 由调用方按序号裁剪)。 */
    public static void AppendInRange(
        IReadOnlyList<Dsh.Core.SessionEvent> decoded, long fromSeq, long toSeqExclusive, List<Dsh.Core.SessionEvent> into)
    {
        foreach (var sessionEvent in decoded)
        {
            if (sessionEvent.Seq >= fromSeq && sessionEvent.Seq < toSeqExclusive)
                into.Add(sessionEvent);
        }
    }
}

