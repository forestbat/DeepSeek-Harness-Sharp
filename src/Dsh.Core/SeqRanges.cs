using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Core;

/**
 * sourceEventSeqs 的存盘编码: 严格递增的一串序号压成 [start, end] 区间, 其余原样。
 * 解码只接受两种形状: 数字, 或两元素的 [start, end] 区间; 带区间时结果必须严格递增。
 * 与写入侧 Encode 互为逆运算 —— 两处必须一起改。
 */
public static class SeqRanges
{
    private const int MinRangeLength = 3;
    private const long Unlimited = long.MaxValue;

    public static JsonArray Encode(IReadOnlyList<long> values)
    {
        if (!IsStrictlyIncreasing(values))
        {
            var verbatim = new JsonArray();
            foreach (var value in values) verbatim.Add(value);
            return verbatim;
        }
        var encoded = new JsonArray();
        var start = 0;
        while (start < values.Count)
        {
            var end = start;
            while (end + 1 < values.Count && values[end + 1] == values[end] + 1) end += 1;
            if (end - start >= MinRangeLength - 1) encoded.Add(new JsonArray(values[start], values[end]));
            else for (var index = start; index <= end; index += 1) encoded.Add(values[index]);
            start = end + 1;
        }
        return encoded;
    }

    /**
     * 从 reader 展开编码形式; reader 必须正停在数组的起始处。
     * maxEntries 是合法上界(调用方通常给事件自身的序号), 超出即视为损坏: 一个事件不可能引用比自己还靠后的序号。
     */
    public static IReadOnlyList<long> Decode(ref Utf8JsonReader reader, long maxEntries = Unlimited)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new FormatException("sourceEventSeqs must be an array");
        var decoded = new List<long>();
        var hasRange = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                var seq = ReadSeq(ref reader);
                if (decoded.Count >= maxEntries)
                    throw new FormatException("sourceEventSeqs exceeds its event sequence");
                decoded.Add(seq);
                continue;
            }
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new FormatException("sourceEventSeqs range entries must be [start, end] pairs");
            var start = -1L;
            var end = -1L;
            for (var index = 0; reader.Read() && reader.TokenType != JsonTokenType.EndArray; index += 1)
            {
                var value = ReadSeq(ref reader);
                if (index == 0) start = value;
                else if (index == 1) end = value;
                else throw new FormatException("sourceEventSeqs range entries must be [start, end] pairs");
            }
            if (start < 0 || end < 0)
                throw new FormatException("sourceEventSeqs range entries must be [start, end] pairs");
            if (end < start) throw new FormatException("sourceEventSeqs ranges require start <= end");
            if (end - start + 1 > maxEntries - decoded.Count)
                throw new FormatException("sourceEventSeqs range exceeds its event sequence");
            for (var value = start; value <= end; value += 1) decoded.Add(value);
            hasRange = true;
        }
        if (hasRange && !IsStrictlyIncreasing(decoded))
            throw new FormatException("sourceEventSeqs ranges must be strictly increasing");
        return decoded;
    }

    private static long ReadSeq(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var seq) && seq >= 0)
            return seq;
        throw new FormatException("sourceEventSeqs must contain non-negative safe integers");
    }

    private static bool IsStrictlyIncreasing(IReadOnlyList<long> values)
    {
        for (var index = 1; index < values.Count; index += 1)
            if (values[index] <= values[index - 1]) return false;
        return true;
    }
}