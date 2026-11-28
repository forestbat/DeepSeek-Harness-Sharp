using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Persistence;

/**
 * 表面折叠检查点的编码: 节点序号按差值 varint 存, 尾部信息(request/header、request/context)单独一段 JSON。
 *
 * JSONL 后端的 `.surface` 旁车与数据库后端的 `surface_fold` 行共用这一份格式 —— 同一份检查点在两个后端之间
 * 搬动时不至于因为编码不同而失效。
 */
internal static class SurfaceFoldCodec
{
    public static byte[] EncodeNodes(IReadOnlyList<long> nodes)
    {
        var buffer = new List<byte>(nodes.Count * 2);
        long previous = 0;
        foreach (var seq in nodes)
        {
            var delta = seq - previous;
            previous = seq;
            var value = (ulong)delta;
            while (value >= 0x80)
            {
                buffer.Add((byte)(value | 0x80));
                value >>= 7;
            }
            buffer.Add((byte)value);
        }
        return [.. buffer];
    }

    /** 没有节点条数时按负载解到底: 数据库只存事件条数, 不另存节点数。 */
    public static List<long> DecodeAll(byte[] payload) => DecodeNodes(payload, int.MaxValue);

    public static List<long> DecodeNodes(byte[] payload, int count)
    {
        var nodes = new List<long>(Math.Min(count, payload.Length));
        long previous = 0;
        var cursor = 0;
        while (cursor < payload.Length && nodes.Count < count)
        {
            ulong value = 0;
            var shift = 0;
            while (cursor < payload.Length)
            {
                var current = payload[cursor];
                cursor += 1;
                value |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                    break;
                shift += 7;
            }
            previous += (long)value;
            nodes.Add(previous);
        }
        if (nodes.Count != count && count != int.MaxValue)
            throw new FormatException("surface fold nodes are truncated");
        return nodes;
    }

    public static byte[] EncodeMeta(SurfaceFoldSnapshot fold)
    {
        var meta = new SurfaceFoldMeta(fold.RequestHeader, fold.RequestContext);
        return JsonSerializer.SerializeToUtf8Bytes(meta, DshCoreJsonContext.Default.SurfaceFoldMeta);
    }

    public static (EpochHeader? Header, RequestContextPayload? Context) DecodeMeta(byte[] payload)
    {
        try
        {
            var meta = JsonSerializer.Deserialize(payload, DshCoreJsonContext.Default.SurfaceFoldMeta);
            return (meta?.RequestHeader, meta?.RequestContext);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}


