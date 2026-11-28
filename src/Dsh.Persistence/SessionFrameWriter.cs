using System.Buffers;
using System.Text;
using Dsh.Core;

namespace Dsh.Persistence;

/** 一帧的压缩字节, 以及它覆盖的事件段(首序号、条数、帧内 replace 的最小起点)。 */
internal readonly record struct EncodedFrame(byte[] Content, long FirstSeq, int EventCount, long MinReplaceStart);

/**
 * 把一批事件切成帧: JSONL 后端与数据库后端共用这一份切帧规则。
 *
 * 两边必须切出**同样的字节**: `TryReadLogSlice` 的 ContentHash 与导出/导入的往返都靠它,
 * 一份日志在两个后端之间换手时不能因为"帧边界不一样"而变成另一份内容。
 */
internal static class SessionFrameWriter
{
    public static List<EncodedFrame> Encode(IReadOnlyList<SessionEvent> events, bool packChunks, int maxPlaintextBytes, bool compress)
    {
        var records = ChunkRows.EncodeRecords(events, packChunks);
        var frames = new List<EncodedFrame>();
        var frame = new ArrayBufferWriter<byte>();
        long firstSeq = 0;
        var eventCount = 0;
        var minReplaceStart = long.MaxValue;
        foreach (var record in records)
        {
            // 每条记录只编码一次: 编码后的字节数就是它的真实体积, 预算按它结算。
            var line = Encoding.UTF8.GetBytes(record.Line);
            var recordBytes = line.Length + 1;
            // 一个空帧里也要能装下这一行: 单行超过预算时它独占一帧, 否则永远塞不进去。
            if (eventCount > 0 && frame.WrittenCount + recordBytes > maxPlaintextBytes)
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

        EncodedFrame Materialize(ArrayBufferWriter<byte> text, long seq, int count, long replaceStart)
            => new(compress ? ZstdFrames.CompressFrame(text.WrittenSpan) : text.WrittenSpan.ToArray(), seq, count, replaceStart);
    }
}
