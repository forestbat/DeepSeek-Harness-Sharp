using System.Text.Json.Nodes;

namespace Dsh.Persistence;

internal static class SeqRanges
{
    private const int MinRangeLength = 3;

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

    public static List<long> Decode(JsonNode? node, long maxEntries)
    {
        if (node is not JsonArray array) throw new FormatException("sourceEventSeqs must be an array");
        var decoded = new List<long>();
        var hasRange = false;
        foreach (var entry in array)
        {
            if (entry is JsonValue number && number.TryGetValue<long>(out var seq))
            {
                AssertSeq(seq);
                if (decoded.Count >= maxEntries) throw new FormatException("sourceEventSeqs exceeds its event sequence");
                decoded.Add(seq);
                continue;
            }
            if (entry is not JsonArray pair || pair.Count != 2)
                throw new FormatException("sourceEventSeqs range entries must be [start, end] pairs");
            var start = ReadSeq(pair[0]);
            var end = ReadSeq(pair[1]);
            if (end < start) throw new FormatException("sourceEventSeqs ranges require start <= end");
            var length = end - start + 1;
            if (length > maxEntries - decoded.Count)
                throw new FormatException("sourceEventSeqs range exceeds its event sequence");
            for (var value = start; value <= end; value += 1) decoded.Add(value);
            hasRange = true;
        }
        if (hasRange && !IsStrictlyIncreasing(decoded))
            throw new FormatException("sourceEventSeqs ranges must be strictly increasing");
        return decoded;
    }

    private static long ReadSeq(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<long>(out var seq)) return AssertSeq(seq);
        throw new FormatException("sourceEventSeqs must contain non-negative safe integers");
    }

    private static long AssertSeq(long value)
    {
        if (value < 0) throw new FormatException("sourceEventSeqs must contain non-negative safe integers");
        return value;
    }

    private static bool IsStrictlyIncreasing(IReadOnlyList<long> values)
    {
        for (var index = 1; index < values.Count; index += 1)
            if (values[index] <= values[index - 1]) return false;
        return true;
    }
}
