using Dsh.Persistence;

namespace Dsh.SessionDump;

/**
 * 帧级体检: 逐帧用**生产解析器**(SessionLogSliceParser)解码, 打印字节区间与首末序号。
 *
 * 用来定位"序号在一段里倒退"的现场。正常的会话日志是只追加的: 帧与帧之间序号单调递增。
 * 一旦出现倒退, 说明盘上那一段被别的写入者覆盖过(截断后重写的残迹), 按页读会把整段判成不可用。
 *
 * 用法: Dsh.SessionDump <logPath> --frames [aroundByte] [span]
 */
internal static class FrameInspector
{
    private const int DefaultSpan = 6;

    public static int Run(string logPath, long? aroundByte, int span)
    {
        if (!File.Exists(logPath))
        {
            Console.Error.WriteLine($"没有这份日志: {logPath}");
            return 2;
        }
        var bytes = File.ReadAllBytes(logPath);
        var scan = ZstdFrames.Scan(bytes);
        Console.WriteLine($"# bytes {bytes.Length}, scanned frames {scan.Frames.Count}, damaged {scan.Damaged.Count}, tornStart {scan.TornStart}");
        var header = FramePlain(bytes, 0);
        var from = 1;
        var to = scan.Frames.Count - 1;
        if (aroundByte is { } target)
        {
            var hit = Enumerable.Range(1, scan.Frames.Count - 1)
                .FirstOrDefault(slot => bytes.Length > 0 && scan.Frames[slot].Start <= target && scan.Frames[slot].End > target, -1);
            if (hit <= 0)
            {
                Console.WriteLine($"# no frame covers byte {target}");
                return 1;
            }
            from = Math.Max(1, hit - span);
            to = Math.Min(scan.Frames.Count - 1, hit + span);
            Console.WriteLine($"# frame {hit} covers byte {target}; printing {from}..{to}");
        }
        long previousLast = -1;
        for (var slot = from; slot <= to; slot += 1)
        {
            var frame = scan.Frames[slot];
            var plain = FramePlain(bytes, slot);
            var (first, last, count) = Bounds(header, plain, previousLast + 1);
            var regression = previousLast >= 0 && (first < 0 || first < previousLast)
                ? $"  <== 不连续(上一帧末条 {previousLast})"
                : "";
            Console.WriteLine($"frame {slot}: bytes [{frame.Start},{frame.End}) seq {first}..{last} ({count} 事件){regression}");
            if (last >= 0)
                previousLast = last;
        }
        return 0;
    }

    private static byte[] FramePlain(byte[] bytes, int slot)
    {
        var scan = ZstdFrames.Scan(bytes);
        var frame = scan.Frames[slot];
        return ZstdFrames.DecompressFrame(bytes, frame.Start, frame.End - frame.Start, (int)frame.Start);
    }

    /**
     * 直接读行里的序号: 普通事件是 "seq", chunk 打包行是 "seq0"(一行装多条)。
     *
     * 不用生产解析器逐帧解, 是因为它要求帧内序号与传入的起点连续 —— 而这里正是要找出"不连续"的位置。
     */
    private static (long First, long Last, int Count) Bounds(byte[] header, byte[] plain, long expectedFirst)
    {
        long first = -1;
        long last = -1;
        var count = 0;
        var lineStart = 0;
        while (lineStart < plain.Length)
        {
            var newline = plain.AsSpan(lineStart).IndexOf((byte)'\n');
            if (newline < 0)
                break;
            var line = plain.AsSpan(lineStart, newline);
            lineStart += newline + 1;
            if (line.IsEmpty)
                continue;
            var seq = SeqOf(line);
            if (seq < 0)
                continue;
            if (first < 0)
                first = seq;
            last = seq;
            count += Math.Max(1, RecordCount(line));
        }
        _ = expectedFirst;
        return (first, last, count);
    }

    /** chunk 打包行的条数 = texts 数组长度; 普通事件是 1。 */
    private static int RecordCount(ReadOnlySpan<byte> line)
    {
        var marker = "\"texts\":["u8;
        var at = line.IndexOf(marker);
        if (at < 0)
            return 1;
        var tail = line[(at + marker.Length)..];
        var quoted = 0;
        var inString = false;
        var escaped = false;
        foreach (var symbol in tail)
        {
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (symbol == (byte)'\\')
                {
                    escaped = true;
                    continue;
                }
                if (symbol == (byte)'"')
                    inString = false;
                continue;
            }
            if (symbol == (byte)'"')
            {
                inString = true;
                quoted += 1;
                continue;
            }
            if (symbol == (byte)']')
                break;
        }
        return quoted == 0 ? 1 : quoted;
    }

    private static long SeqOf(ReadOnlySpan<byte> line)
    {
        var bySeq = ReadNumber(line, "\"seq\":"u8);
        return bySeq >= 0 ? bySeq : ReadNumber(line, "\"seq0\":"u8);
    }

    private static long ReadNumber(ReadOnlySpan<byte> line, ReadOnlySpan<byte> marker)
    {
        var at = line.IndexOf(marker);
        if (at < 0)
            return -1;
        var cursor = at + marker.Length;
        long value = 0;
        var digits = 0;
        while (cursor < line.Length && line[cursor] >= (byte)'0' && line[cursor] <= (byte)'9')
        {
            value = (value * 10) + (line[cursor] - '0');
            cursor += 1;
            digits += 1;
        }
        return digits == 0 ? -1 : value;
    }
}