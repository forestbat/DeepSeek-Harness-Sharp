using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Persistence;

/**
 * 会话日志的物理索引: 每个帧覆盖的字节区间与事件区间(seq)。
 *
 * 有了它, "翻历史 / 跳转 / 只取最近一屏"就是解压命中帧的缺页重读, 而不是把整份日志展开一遍;
 * 1M 事件的现场日志因此从 23 秒全量展开变成几十毫秒取一窗。
 *
 * 落盘形态是日志旁边的 `日志文件名.frames`, 紧凑二进制:
 *   16 字节头 = 8 字节魔数 "DSHFRAM2" + int32 版本 + int32 记录宽度;
 *   之后每条帧记录 36 字节(Start/End/FirstSeq/EventCount/MinReplaceStart, 小端)。
 * 条目数由文件长度推出, 所以追加不必改写头。
 * 旧版本(28 字节记录)与 JSON 行索引都能读: 前者视为"没有 replace 信息", 后者读一次就按新格式重写。
 *
 * MinReplaceStart 是这一帧里所有 surface replace 指向的最小序号(没有则 long.MaxValue)。
 * 它是**写侧本来就知道的事实**: 读侧据此在解码前就能算准"尾窗要退到哪", 不必解压一遍再回头补,
 * 也不必用"平均每事件多少字节"这种预估去猜。
 *
 * 校验只看结构: 首条帧从字节 0 开始, 末条帧的 end 等于日志当前长度 —— 少了(截断/重写)、多了(半帧)都判失效,
 * 于是 revert、修复、崩溃半帧都会自然退回全量解析并重建索引。
 *
 * 同一进程内同一份日志只解析一次: 解析结果连同两个文件的(长度, 修改时间)缓存起来, 之后每次 TryLoad 只做两次 stat。
 * 任何写动作(Save/Append/Delete)都让缓存失效 —— 落盘的索引变了, 内存里那份就不能再被当成最新的。
 */
public readonly record struct FrameEntry(long Start, long End, long FirstSeq, int EventCount)
{
    /** 这一帧里所有 replace 指向的最小序号; long.MaxValue 表示"这一帧里没有 replace"。 */
    public long MinReplaceStart { get; init; } = long.MaxValue;
}

public sealed class SessionLogIndex
{
    public const string Extension = ".frames";

    private const int FormatVersion = 3;
    private const int HeaderSize = 16;
    private const int RecordSize = (sizeof(long) * 4) + sizeof(int);

    /** 上一版记录宽度: 只有事件区间, 没有 replace 信息。 */
    private const int LegacyRecordSize = (sizeof(long) * 3) + sizeof(int);

    private static ReadOnlySpan<byte> Magic => "DSHFRAM2"u8;

    public required long Length { get; init; }
    public required long EventCount { get; init; }
    public required long SeqBase { get; init; }
    public required IReadOnlyList<FrameEntry> Frames { get; init; }

    public static string PathFor(string logPath) => $"{logPath}{Extension}";

    /** 取证用: 本进程里索引真正被读盘解析的次数与命中缓存的次数(测试与探针读, 生产代码不读)。 */
    internal static long ParseCount => Interlocked.Read(ref _parseCount);

    internal static long CacheHitCount => Interlocked.Read(ref _cacheHitCount);

    /** 取证用: 解析累计花掉的毫秒(含读盘与建条目)。 */
    internal static long ParseMilliseconds => Interlocked.Read(ref _parseMilliseconds);

    private static long _parseCount;
    private static long _cacheHitCount;
    private static long _parseMilliseconds;

    internal static void ResetLoadCounters()
    {
        Interlocked.Exchange(ref _parseCount, 0);
        Interlocked.Exchange(ref _cacheHitCount, 0);
        Interlocked.Exchange(ref _parseMilliseconds, 0);
    }

    /** 覆盖 [offset, offset + count) 的帧下标区间(含两端)。 */
    public (int From, int To) FramesCovering(long offset, long count)
    {
        var end = offset + count;
        var from = -1;
        var to = -1;
        for (var index = 0; index < Frames.Count; index += 1)
        {
            var frame = Frames[index];
            if (frame.EventCount == 0)
                continue;
            var frameEnd = frame.FirstSeq + frame.EventCount;
            if (frameEnd <= offset || frame.FirstSeq >= end)
                continue;
            if (from < 0)
                from = index;
            to = index;
        }
        return from < 0 ? (-1, -1) : (from, to);
    }

    public static SessionLogIndex? TryLoad(string logPath)
    {
        var path = PathFor(logPath);
        var stamp = Stat(path);
        if (stamp is not { } indexFile)
        {
            Forget(path);
            return null;
        }
        var logLength = LengthOf(logPath);
        if (logLength is null)
            return null;
        if (Cache.TryGetValue(path, out var cached) && cached.Stamp == indexFile)
        {
            if (cached.Index.Length == logLength)
            {
                Interlocked.Increment(ref _cacheHitCount);
                return cached.Index;
            }
            // 索引文件没动、日志却变了: 这份索引不覆盖现在的日志, 读一遍也只会得出同样的结论。
            return null;
        }
        if (Read(indexFile.Path, indexFile.Bytes, logLength.Value) is not { } index)
        {
            Forget(path);
            return null;
        }
        // 解析期间文件可能又被追加: 只有两次读到的戳记一致才缓存, 否则下一次 stat 自然会对不上。
        if (Stat(path) is { } settled && settled == indexFile)
            Cache[path] = new Cached(index, indexFile);
        return index;
    }

    private static SessionLogIndex? Read(string path, long indexBytes, long logLength)
    {
        if (indexBytes < HeaderSize)
            return null;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
        if (bytes.Length < HeaderSize)
            return null;
        Interlocked.Increment(ref _parseCount);
        var clock = Stopwatch.StartNew();
        try
        {
            return Decode(path, bytes, logLength);
        }
        finally
        {
            Interlocked.Add(ref _parseMilliseconds, clock.ElapsedMilliseconds);
        }
    }

    private static SessionLogIndex? Decode(string path, byte[] bytes, long logLength)
    {
        var legacy = !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic);
        if ((legacy ? ReadLegacyJson(bytes) : ReadRecords(bytes)) is not { Length: > 0 } frames)
            return null;
        if (frames[0].Start != 0 || frames[^1].End != logLength)
            return null;
        // 结构自洽校验: 事件帧的序号区间必须首尾相接、逐帧递增。
        // 校验不过就判失效(交给全量读重建) —— 一份"帧边界对、条数错"的索引比没有索引更糟:
        // 尾窗会按错的条数往回数, 一路退到文件开头(实测 d 档 2 万条的窗口变成 32.7 万条)。
        var nextSeq = frames[0].FirstSeq;
        for (var position = 0; position < frames.Length; position += 1)
        {
            var frame = frames[position];
            // 第 0 帧是头行, 不含事件; 之后每一帧都必须含事件 —— 一帧是一条完整记录, 不可能为空。
            if (position == 0)
                continue;
            if (frame.EventCount <= 0)
                return null;
            if (frame.FirstSeq != nextSeq)
                return null;
            nextSeq += frame.EventCount;
        }
        var index = new SessionLogIndex
        {
            Length = logLength,
            EventCount = nextSeq,
            SeqBase = frames[0].FirstSeq,
            Frames = frames,
        };
        if (legacy)
            Upgrade(path, index);
        return index;
    }

    /** 读到旧 JSON 索引时按新格式重写一次; 写不成就算了, 下次读还能再试。 */
    private static void Upgrade(string path, SessionLogIndex index)
    {
        try
        {
            index.SaveCore(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }
        // 刚写出去的那一份就是缓存该有的样子: 不这样做, 下一次 TryLoad 会因为戳记变了又解析一遍。
        if (Stat(path) is { } stamp)
            Cache[path] = new Cached(index, stamp);
    }

    private static FrameEntry[]? ReadRecords(byte[] bytes)
    {
        var version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        var recordSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        // 上一版(v2, 28 字节记录)照旧可读: 那一版没有 replace 信息, 读侧自然退回"解压后再看"。
        if (recordSize != RecordSize && !(version == 2 && recordSize == LegacyRecordSize))
            return null;
        if (version != FormatVersion && version != 2)
            return null;
        // 末尾半个记录(崩在写索引时)不算条目: 结构校验会决定是否需要重建。
        var count = (bytes.Length - HeaderSize) / recordSize;
        var frames = new FrameEntry[count];
        for (var position = 0; position < count; position += 1)
            frames[position] = ReadRecord(bytes.AsSpan(HeaderSize + (position * recordSize), recordSize), recordSize);
        return frames;
    }

    private static FrameEntry[]? ReadLegacyJson(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8);
        if (reader.ReadLine() is not { } head)
            return null;
        IndexHeader? header;
        try
        {
            header = JsonSerializer.Deserialize<IndexHeader>(head, Options);
        }
        catch (JsonException)
        {
            return null;
        }
        if (header is not { Version: 1 })
            return null;
        var frames = new List<FrameEntry>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
                continue;
            FrameEntryRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<FrameEntryRecord>(line, Options);
            }
            catch (JsonException)
            {
                // 半个追加行(崩溃在写索引时): 忽略它, 让结构校验决定是否需要重建。
                break;
            }
            if (record is null)
                break;
            frames.Add(new FrameEntry(record.Start, record.End, record.First, record.Count));
        }
        return [.. frames];
    }

    private static FrameEntry ReadRecord(ReadOnlySpan<byte> record, int recordSize) => new(
        BinaryPrimitives.ReadInt64LittleEndian(record),
        BinaryPrimitives.ReadInt64LittleEndian(record[sizeof(long)..]),
        BinaryPrimitives.ReadInt64LittleEndian(record[(sizeof(long) * 2)..]),
        BinaryPrimitives.ReadInt32LittleEndian(record[(sizeof(long) * 3)..]))
    {
        // 上一版没有这一项: 保持 MaxValue, 读侧就知道"这一帧的 replace 情况未知"。
        MinReplaceStart = recordSize >= RecordSize
            ? BinaryPrimitives.ReadInt64LittleEndian(record[(sizeof(long) * 3 + sizeof(int))..])
            : long.MaxValue,
    };

    private static void WriteRecord(Span<byte> record, FrameEntry entry)
    {
        BinaryPrimitives.WriteInt64LittleEndian(record, entry.Start);
        BinaryPrimitives.WriteInt64LittleEndian(record[sizeof(long)..], entry.End);
        BinaryPrimitives.WriteInt64LittleEndian(record[(sizeof(long) * 2)..], entry.FirstSeq);
        BinaryPrimitives.WriteInt32LittleEndian(record[(sizeof(long) * 3)..], entry.EventCount);
        BinaryPrimitives.WriteInt64LittleEndian(record[(sizeof(long) * 3 + sizeof(int))..], entry.MinReplaceStart);
    }

    /** 索引整体重写(建立时用)。 */
    public void Save(string logPath)
    {
        SaveCore(PathFor(logPath));
        Forget(PathFor(logPath));
    }

    /**
     * 只写索引文件, 不碰缓存。
     * 旧 JSON 升级时用它: 升级发生在读路径里, 没经过调用方, 缓存由 TryLoad 自己填。
     */
    private void SaveCore(string path)
    {
        var bytes = new byte[HeaderSize + (Frames.Count * RecordSize)];
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), RecordSize);
        for (var position = 0; position < Frames.Count; position += 1)
            WriteRecord(bytes.AsSpan(HeaderSize + (position * RecordSize), RecordSize), Frames[position]);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    /** 追加一批帧记录(写日志的同时增量维护索引)。 */
    public static void Append(string logPath, IReadOnlyList<FrameEntry> frames, long eventCount, long seqBase)
    {
        var path = PathFor(logPath);
        var buffer = new byte[frames.Count * RecordSize];
        for (var position = 0; position < frames.Count; position += 1)
            WriteRecord(buffer.AsSpan(position * RecordSize, RecordSize), frames[position]);
        if (!File.Exists(path))
        {
            using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var header = new byte[HeaderSize];
            Magic.CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), FormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), RecordSize);
            created.Write(header);
            created.Write(buffer);
            created.Flush(true);
        }
        else
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(buffer);
            stream.Flush(true);
        }
        Forget(path);
    }

    public static void Delete(string logPath)
    {
        var path = PathFor(logPath);
        Forget(path);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static void Forget(string path) => Cache.TryRemove(path, out _);

    private static FileStamp? Stat(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        return file.Exists ? new FileStamp(file.FullName, file.Length, file.LastWriteTimeUtc) : null;
    }

    private static long? LengthOf(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static readonly ConcurrentDictionary<string, Cached> Cache = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly record struct FileStamp(string Path, long Bytes, DateTime WriteTimeUtc);

    private sealed record Cached(SessionLogIndex Index, FileStamp Stamp);

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private sealed record IndexHeader(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("events")] long Events,
        [property: JsonPropertyName("seqBase")] long SeqBase);

    private sealed record FrameEntryRecord(
        [property: JsonPropertyName("start")] long Start,
        [property: JsonPropertyName("end")] long End,
        [property: JsonPropertyName("first")] long First,
        [property: JsonPropertyName("count")] int Count);
}