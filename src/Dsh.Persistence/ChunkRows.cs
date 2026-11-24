using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Persistence;

internal static class ChunkRows
{
    public const string TextChunksTag = "text-chunks";
    public const string ReasoningChunksTag = "reasoning-chunks";
    public const string ToolCallChunksTag = "tool-call-chunks";
    private const int MinRunLength = 3;

    private enum DeltaKind
    {
        Text,
        Reasoning,
        ToolCall,
    }

    private sealed record PackedChunkRow(
        string RowType,
        long Seq0,
        long Time0,
        int Turn,
        int Step,
        int Index,
        long[] Dt,
        string[] Members,
        string? CallId,
        string? CallName);

    public static string EncodeEventLines(IReadOnlyList<SessionEvent> events, bool packChunks)
        => string.Join('\n', EncodeRecords(events, packChunks).Select(record => record.Line));

    /**
     * 把一批事件编成存储行, 连同每行覆盖的事件(行首序号与条数)一起返回。
     *
     * 一行在盘上是**一条记录**: 普通事件各占一行, 连续的同类 chunk 被打包成一行(见 PackRuns)。
     * 写侧按行的真实字节切帧, 所以这里必须把"一行代表几条事件"带出来 —— 索引要记的是事件条数,
     * 帧边界也只能落在行边界上(把一行劈开就成了读不回来的残缺记录)。
     */
    public static IReadOnlyList<EncodedRecord> EncodeRecords(IReadOnlyList<SessionEvent> events, bool packChunks)
    {
        var records = packChunks ? PackRuns(events) : events.Cast<object>().ToList();
        var encoded = new List<EncodedRecord>(records.Count);
        foreach (var record in records)
        {
            if (record is PackedChunkRow row)
                encoded.Add(new EncodedRecord(SerializeRow(row), row.Seq0, row.Members.Length, long.MaxValue));
            else
            {
                var sessionEvent = (SessionEvent)record;
                // 打包行进不来这里(SurfaceOp 非空的事件不可打包), 所以 replace 只会出现在普通记录上。
                var replaceStart = sessionEvent.SurfaceOp is SurfaceOp.Replace replace ? replace.Start : long.MaxValue;
                encoded.Add(new EncodedRecord(SerializeEvent(sessionEvent), sessionEvent.Seq, 1, replaceStart));
            }
        }
        return encoded;
    }

    /**
     * 一帧明文里有多少条事件、首条序号是多少 —— **只扫描, 不物化 payload**。
     *
     * 窗口化要先知道每帧覆盖多少条事件才能决定尾窗起点, 而那一步如果走完整解码,
     * 就会把整份日志的事件对象全部物化一遍, 之后正式取窗口再物化一遍(实测 327,855 条解了两遍)。
     * 这里只读 type/seq/seq0 与打包行的成员数组长度, 不建 SessionEvent。
     */
    internal static (long FirstSeq, int EventCount) MeasureStorageRecords(ReadOnlySpan<byte> plain)
    {
        long firstSeq = -1;
        var total = 0;
        var lineStart = 0;
        while (lineStart < plain.Length)
        {
            var newline = plain[lineStart..].IndexOf((byte)'\n');
            if (newline < 0)
                break;
            var lineEnd = lineStart + newline;
            if (lineEnd > lineStart)
            {
                var (seq, count) = MeasureStorageRecord(plain[lineStart..lineEnd]);
                if (firstSeq < 0)
                    firstSeq = seq;
                total += count;
            }
            lineStart = lineEnd + 1;
        }
        return (firstSeq, total);
    }

    /** 一条存储记录的(首序号, 事件条数); 打包行的条数来自它的成员数组长度。 */
    /**
     * 一条存储记录的**信封**: 类型 + 每条的序号与时刻 + 这一行在明文里的位置。
     *
     * 只读信封不解负载: 打包行只读 `seq0/time0/dt`(跳过 texts/args), 普通行只读 `type/seq/time`
     * (跳过 data)。于是"从头扫到尾"只需要查表, 真正要渲染的那几条才按位置去解那一条记录。
     */
    internal readonly record struct StoredEnvelope(long Seq, long Time, string Type, int LineStart, int LineLength);

    /** 把一段明文的每一条事件读成信封(不解负载)。 */
    internal static void ReadEnvelopes(ReadOnlySpan<byte> plain, List<StoredEnvelope> into)
    {
        var lineStart = 0;
        while (lineStart < plain.Length)
        {
            var newline = plain[lineStart..].IndexOf((byte)'\n');
            if (newline < 0)
                break;
            var lineEnd = lineStart + newline;
            if (lineEnd > lineStart)
                ReadEnvelope(plain[lineStart..lineEnd], lineStart, lineEnd - lineStart, into);
            lineStart = lineEnd + 1;
        }
    }

    private static void ReadEnvelope(ReadOnlySpan<byte> line, int lineStart, int lineLength, List<StoredEnvelope> into)
    {
        var tag = ChunkRowTagOf(line);
        if (tag is not null)
        {
            ReadPackedEnvelope(line, tag, lineStart, lineLength, into);
            return;
        }
        string? type = null;
        long seq = -1;
        long time = 0;
        var reader = new Utf8JsonReader(line);
        try
        {
            
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    type = reader.GetString();
                }
                else if (reader.ValueTextEquals("seq"u8))
                {
                    reader.Read();
                    seq = reader.GetInt64();
                }
                else if (reader.ValueTextEquals("time"u8))
                {
                    reader.Read();
                    time = reader.GetInt64();
                }
                else
                {
                    // data 与别的字段都不必读内容, 跳过即可(不建 JsonElement, 不建负载)。
                    reader.Read();
                    reader.Skip();
                }
            }
        }
        catch (JsonException error)
        {
            if (Environment.GetEnvironmentVariable("DSH_ENVELOPE_TRACE") is { Length: > 0 })
                Console.Error.WriteLine($"[env] json {error.Message} line={System.Text.Encoding.UTF8.GetString(line[..Math.Min(120, line.Length)])}");
            return;
        }
        if (type is null || seq < 0)
        {
            if (Environment.GetEnvironmentVariable("DSH_ENVELOPE_TRACE") is { Length: > 0 })
                Console.Error.WriteLine($"[env] skip type={type ?? "-"} seq={seq} line={System.Text.Encoding.UTF8.GetString(line[..Math.Min(120, line.Length)])}");
            return;
        }
        into.Add(new StoredEnvelope(seq, time, type, lineStart, lineLength));
    }

    /** 打包行: 类型固定是 assistant/chunk, 每条的时刻是 time0 加上前面各条的 dt 之和。 */
    private static void ReadPackedEnvelope(ReadOnlySpan<byte> line, string tag, int lineStart, int lineLength,
        List<StoredEnvelope> into)
    {
        var seq0 = -1L;
        var time0 = 0L;
        long[]? gaps = null;
        var members = 0;
        var reader = new Utf8JsonReader(line);
        try
        {
            
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("seq0"u8))
                {
                    reader.Read();
                    seq0 = reader.GetInt64();
                }
                else if (reader.ValueTextEquals("time0"u8))
                {
                    reader.Read();
                    time0 = reader.GetInt64();
                }
                else if (reader.ValueTextEquals("data"u8))
                {
                    reader.Read();
                    (gaps, members) = ReadPackedTimes(ref reader);
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
        }
        catch (JsonException error)
        {
            if (Environment.GetEnvironmentVariable("DSH_ENVELOPE_TRACE") is { Length: > 0 })
                Console.Error.WriteLine($"[env-packed] json {error.Message} line={System.Text.Encoding.UTF8.GetString(line[..Math.Min(100, line.Length)])}");
            return;
        }
        if (seq0 < 0 || members <= 0 || gaps is null)
        {
            if (Environment.GetEnvironmentVariable("DSH_ENVELOPE_TRACE") is { Length: > 0 })
                Console.Error.WriteLine($"[env-packed] seq0={seq0} members={members} gaps={gaps?.Length.ToString() ?? "-"} line={System.Text.Encoding.UTF8.GetString(line[..Math.Min(100, line.Length)])}");
            return;
        }
        var time = time0;
        for (var index = 0; index < members; index += 1)
        {
            if (index > 0)
            {
                try { time = checked(time + gaps[index - 1]); }
                catch (OverflowException) { return; }
            }
            into.Add(new StoredEnvelope(seq0 + index, time, SessionEventTypes.AssistantChunk, lineStart, lineLength));
        }
    }

    /** 打包行的 dt 数组(成员之间的时刻差)与成员条数; texts/args 一个字节都不读。 */
    private static (long[] Gaps, int Members) ReadPackedTimes(ref Utf8JsonReader reader)
    {
        long[]? gaps = null;
        // 调用方刚读完 "data" 的值, reader 已停在 StartObject 上, 直接读它的属性。
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("dt"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray)
                    continue;
                var values = new List<long>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    values.Add(reader.GetInt64());
                gaps = [.. values];
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
        // 成员条数 = dt 的条数 + 1; 没有 dt 就当一条。
        return (gaps ?? [], gaps is null ? 1 : gaps.Length + 1);
    }

    internal static (long FirstSeq, int EventCount) MeasureStorageRecord(ReadOnlySpan<byte> line)
    {
        long seq = -1;
        var count = 0;
        var inMembers = false;
        var reader = new Utf8JsonReader(line);
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName when reader.ValueTextEquals("seq"u8) && seq < 0:
                    case JsonTokenType.PropertyName when reader.ValueTextEquals("seq0"u8) && seq < 0:
                        reader.Read();
                        seq = reader.TokenType == JsonTokenType.Number ? reader.GetInt64() : 0;
                        break;
                    case JsonTokenType.PropertyName when reader.ValueTextEquals("texts"u8) || reader.ValueTextEquals("args"u8):
                        reader.Read();
                        if (reader.TokenType == JsonTokenType.StartArray)
                            inMembers = true;
                        break;
                    case JsonTokenType.String when inMembers:
                        count += 1;
                        break;
                    case JsonTokenType.EndArray:
                        inMembers = false;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // 解不出来交给真正的解码路径去报错: 这里只负责"大致有多少条"。
            return (seq < 0 ? 0 : seq, 0);
        }
        // 普通事件没有成员数组: 它就是一条。
        return (seq < 0 ? 0 : seq, count > 0 ? count : 1);
    }

    /** 一条存储行原文, 以及它在序号轴上覆盖的起点与条数。
     * MinReplaceStart 是这条记录里 replace 指向的序号(没有则 long.MaxValue): 写侧顺手带上,
     * 帧索引据此记下"这一帧有没有指向更早节点的 replace", 读侧才算得准尾窗起点。
     */
    internal readonly record struct EncodedRecord(string Line, long FirstSeq, int EventCount, long MinReplaceStart);

    public static IReadOnlyList<SessionEvent> DecodeStorageRecord(ReadOnlySpan<byte> line)
    {
        if (ChunkRowTagOf(line) is { } tag)
            return ExpandRow(line, tag);
        ValidateTypeTag(line);
        var sessionEvent = JsonSerializer.Deserialize(line, DshJson.Info<SessionEvent>())
            ?? throw new FormatException("stored session record deserialized to null");
        return [sessionEvent];
    }

    /**
     * 窗口读取专用: 只解出信封(type/seq/time/ignorable/surfaceOp), `data` 留到访问时再解。
     *
     * 事件持有的是"这一帧明文数组里的一条记录", 一帧的记录共用同一个数组, 所以每条事件不额外分配字节。
     * 全量读取不走这里 —— 那条路径要把整份日志留在内存里, 逐条留原始记录等于明文再存一份。
     */
    public static IReadOnlyList<SessionEvent> DecodeStorageRecordLazy(ReadOnlyMemory<byte> record)
    {
        var span = record.Span;
        // chunk 打包行要展开成多条, 没有"一段连续明文"可用, 只能当场物化。
        if (ChunkRowTagOf(span) is { } tag)
            return ExpandRow(span, tag);
        ValidateTypeTag(span);
        return [ParseEnvelope(record)];
    }

    private static SessionEvent ParseEnvelope(ReadOnlyMemory<byte> record)
    {
        var reader = new Utf8JsonReader(record.Span);
        string? type = null;
        var seq = -1L;
        var time = 0L;
        var ignorable = false;
        SurfaceOp? surfaceOp = null;
        IReadOnlyList<long>? sourceEventSeqs = null;
        var hasData = false;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new FormatException("stored session records must be objects");
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new FormatException("stored session record has an invalid envelope");
            if (reader.ValueTextEquals("type"u8))
            {
                reader.Read();
                type = reader.GetString();
            }
            else if (reader.ValueTextEquals("seq"u8))
            {
                reader.Read();
                seq = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("time"u8))
            {
                reader.Read();
                time = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("ignorable"u8))
            {
                reader.Read();
                ignorable = reader.GetBoolean();
            }
            else if (reader.ValueTextEquals("surfaceOp"u8))
            {
                reader.Read();
                surfaceOp = ReadSurfaceOp(ref reader);
            }
            else if (reader.ValueTextEquals("sourceEventSeqs"u8))
            {
                // 窗口读也要保留来源列表: 跨边界的 replace 要靠它判断"被收掉的那段还在不在"。
                reader.Read();
                sourceEventSeqs = SeqRanges.Decode(ref reader, seq >= 0 ? seq : long.MaxValue);
            }
            else if (reader.ValueTextEquals("data"u8))
            {
                reader.Read();
                hasData = true;
                reader.Skip();
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
        if (type is null || seq < 0 || !hasData)
            throw new FormatException("stored session record has an invalid envelope");
        return new SessionEvent(type, seq, time, ignorable, surfaceOp, null, record) { SourceEventSeqs = sourceEventSeqs };
    }

    private static SurfaceOp ReadSurfaceOp(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.ValueTextEquals("append"u8)
                ? new SurfaceOp.Append()
                : throw new FormatException("invalid surfaceOp");
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new FormatException("invalid surfaceOp");
        var replace = false;
        var start = 0L;
        var end = 0L;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("op"u8))
            {
                reader.Read();
                replace = reader.ValueTextEquals("replace"u8);
            }
            else if (reader.ValueTextEquals("start"u8))
            {
                reader.Read();
                start = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("end"u8))
            {
                reader.Read();
                end = reader.GetInt64();
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
        return replace ? new SurfaceOp.Replace(start, end) : throw new FormatException("invalid replace surfaceOp");
    }

    /** 未转义的 UTF-8 字节与常量比较: type 标签都是纯 ASCII, 不需要解码成字符串。 */
    private static readonly byte[] RequestHeaderType = Encoding.UTF8.GetBytes(SessionEventTypes.RequestHeader);

    private static bool IsType(ReadOnlySpan<byte> value, ReadOnlySpan<byte> expected) => value.SequenceEqual(expected);

    /**
     * 快速路径上的类型标签校验: 只拒绝**已退役的目录内格式**, 未知类型照样收下。
     *
     * 未知类型(多半来自更新的 harness)不再拒绝解释: 它们被解码成不透明记录原样保留,
     * 序号不重排, 于是 sourceEventSeqs / surfaceOp.start 这类"指向别的序号"的字段仍然对得上。
     * 只顺序扫到 type 与 request/header 的 data.reason 就停, 不建树。
     */
    private static void ValidateTypeTag(ReadOnlySpan<byte> line)
    {
        ReadOnlySpan<byte> type = default;
        bool fallback = false;
        var reader = new Utf8JsonReader(line);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new FormatException("stored session records must be objects");
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    type = reader.TokenType == JsonTokenType.String ? reader.ValueSpan : default;
                }
                else if (reader.ValueTextEquals("data"u8))
                {
                    // 只有 request/header 的 data.reason 需要看, 用 Skip 跳过大块正文。
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                        {
                            if (reader.ValueTextEquals("reason"u8))
                            {
                                reader.Read();
                                fallback = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("fallback"u8);
                            }
                            else
                            {
                                reader.Read();
                                reader.Skip();
                            }
                        }
                    }
                    else
                    {
                        reader.Skip();
                    }
                    continue;
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                    continue;
                }
                // 只有 request/header 才需要继续扫 data.reason(退役格式要拒绝); 其余类型(含未知类型)到此为止。
                if (!type.IsEmpty && !IsType(type, RequestHeaderType))
                    break;
            }
        }
        catch (JsonException error)
        {
            throw new FormatException("corrupt session log: stored record is not valid JSON", error);
        }
        if (type.IsEmpty)
            return;
        // 只有退役格式(request/header 的 reason="fallback")要拒绝; 其余类型(含本 harness 不认识的)一律放行,
        // 未知类型由 SessionEventCodec 解成不透明记录原样保留。
        if (IsType(type, RequestHeaderType) && fallback)
            throw new SessionFormatUnsupportedException(
                "session log contains a request/header event with the unsupported legacy reason \"fallback\"; refusing to interpret the log — it was written by a retired pre-release harness");
    }

    private static string SerializeEvent(SessionEvent sessionEvent)
    {
        var node = DshJson.ToNode(sessionEvent)!.AsObject();
        if (sessionEvent.SourceEventSeqs is { } seqs)
            node["sourceEventSeqs"] = SeqRanges.Encode(seqs);
        return node.ToJsonString();
    }

    private static List<object> PackRuns(IReadOnlyList<SessionEvent> events)
    {
        var output = new List<object>();
        DeltaKind? kind = null;
        var run = new List<SessionEvent>();
        foreach (var sessionEvent in events)
        {
            var current = Classify(sessionEvent);
            if (current is null)
            {
                Flush();
                output.Add(sessionEvent);
                continue;
            }
            if (current == kind && run.Count > 0 && Continues(run[^1], sessionEvent, current.Value))
            {
                run.Add(sessionEvent);
                continue;
            }
            Flush();
            kind = current;
            run.Add(sessionEvent);
        }
        Flush();
        return output;

        void Flush()
        {
            if (kind is not null && run.Count >= MinRunLength) output.Add(BuildRow(kind.Value, run));
            else output.AddRange(run);
            kind = null;
            run.Clear();
        }
    }

    private static DeltaKind? Classify(SessionEvent sessionEvent)
    {
        if (sessionEvent.Type != SessionEventTypes.AssistantChunk) return null;
        if (sessionEvent.Ignorable || sessionEvent.SurfaceOp is not null || sessionEvent.SourceEventSeqs is not null) return null;
        if (sessionEvent.Seq < 0) return null;
        return sessionEvent.Data is AssistantChunkPayload payload
            ? payload.Chunk switch
            {
                StreamChunk.TextDelta => DeltaKind.Text,
                StreamChunk.ReasoningDelta => DeltaKind.Reasoning,
                StreamChunk.ToolCallDelta => DeltaKind.ToolCall,
                _ => null,
            }
            : null;
    }

    /**
     * 这条事件会不会被打包进一行(而不是自己占一行)。
     * 写侧的字节预算要按打包后的形态估算 —— 一段连续 chunk 在盘上只有一行, 外壳只付一次。
     */
    internal static bool IsPackable(SessionEvent sessionEvent)
        => Classify(sessionEvent) is not null;

    /**
     * next 是否接着 prev 的同一次流式输出: 是的话它在盘上只是同一行里的一个成员, 不另付外壳。
     * 与 PackRuns 的合并条件同源(同 turn/step、同 block、序号连续), 于是逐条估算与整批编码对同一件事说同一句话。
     */
    internal static bool ContinuesRun(SessionEvent prev, SessionEvent next)
    {
        if (Classify(prev) is not { } kind || Classify(next) is not { } nextKind || kind != nextKind)
            return false;
        return Continues(prev, next, kind);
    }

    private static bool Continues(SessionEvent prev, SessionEvent next, DeltaKind kind)
    {
        if (next.Seq != prev.Seq + 1) return false;
        var prevPayload = (AssistantChunkPayload)prev.Data;
        var nextPayload = (AssistantChunkPayload)next.Data;
        if (nextPayload.Turn != prevPayload.Turn || nextPayload.Step != prevPayload.Step) return false;
        if (kind == DeltaKind.ToolCall)
        {
            var a = (StreamChunk.ToolCallDelta)prevPayload.Chunk;
            var b = (StreamChunk.ToolCallDelta)nextPayload.Chunk;
            return b.Index == a.Index && a.Id == b.Id && a.Name == b.Name;
        }
        return BlockIndex(nextPayload.Chunk) == BlockIndex(prevPayload.Chunk);
    }

    private static int BlockIndex(StreamChunk chunk) => chunk switch
    {
        StreamChunk.TextDelta text => text.Index,
        StreamChunk.ReasoningDelta reasoning => reasoning.Index,
        StreamChunk.ToolCallDelta toolCall => toolCall.Index,
        _ => throw new ArgumentException($"chunk type {chunk.Type} carries no block index"),
    };

    private static PackedChunkRow BuildRow(DeltaKind kind, List<SessionEvent> run)
    {
        var first = (AssistantChunkPayload)run[0].Data;
        var dt = new long[run.Count - 1];
        for (var index = 1; index < run.Count; index += 1)
            dt[index - 1] = run[index].Time - run[index - 1].Time;
        var rowType = kind switch
        {
            DeltaKind.Text => TextChunksTag,
            DeltaKind.Reasoning => ReasoningChunksTag,
            _ => ToolCallChunksTag,
        };
        if (kind == DeltaKind.ToolCall)
        {
            var call = (StreamChunk.ToolCallDelta)first.Chunk;
            var args = run.Select(member => ((StreamChunk.ToolCallDelta)((AssistantChunkPayload)member.Data).Chunk).ArgumentsDelta).ToArray();
            return new PackedChunkRow(rowType, run[0].Seq, run[0].Time, first.Turn, first.Step, call.Index, dt, args, call.Id.Value, call.Name);
        }
        var texts = run.Select(member => ((AssistantChunkPayload)member.Data).Chunk switch
        {
            StreamChunk.TextDelta text => text.Text,
            StreamChunk.ReasoningDelta reasoning => reasoning.Text,
            _ => throw new InvalidOperationException("mixed chunk kinds in one packed run"),
        }).ToArray();
        return new PackedChunkRow(rowType, run[0].Seq, run[0].Time, first.Turn, first.Step, BlockIndex(first.Chunk), dt, texts, null, null);
    }

    private static string SerializeRow(PackedChunkRow row)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", row.RowType);
            writer.WriteNumber("seq0", row.Seq0);
            writer.WriteNumber("time0", row.Time0);
            writer.WriteStartObject("data");
            writer.WriteNumber("turn", row.Turn);
            writer.WriteNumber("step", row.Step);
            writer.WriteNumber("index", row.Index);
            writer.WriteStartArray("dt");
            foreach (var gap in row.Dt) writer.WriteNumberValue(gap);
            writer.WriteEndArray();
            if (row.CallId is not null)
            {
                writer.WriteString("id", row.CallId);
                if (row.CallName is not null) writer.WriteString("name", row.CallName);
            }
            writer.WriteStartArray(row.CallId is null ? "texts" : "args");
            foreach (var member in row.Members) writer.WriteStringValue(member);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /**
     * 打包行的类型标签: 命中三个 chunk 行标签之一才返回, 其余记录不在这里处理。
     * 只扫到 type 为止(序列化时 type 是第一个属性), 不建树、不把整行解成字符串。
     */
    private static string? ChunkRowTagOf(ReadOnlySpan<byte> line)
    {
        var reader = new Utf8JsonReader(line);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.String)
                        return null;
                    if (reader.ValueTextEquals(TextChunksTag)) return TextChunksTag;
                    if (reader.ValueTextEquals(ReasoningChunksTag)) return ReasoningChunksTag;
                    if (reader.ValueTextEquals(ToolCallChunksTag)) return ToolCallChunksTag;
                    return null;
                }
                reader.Read();
                reader.Skip();
            }
        }
        catch (JsonException)
        {
            // 读不出标签就交给后面的解析路径报错, 这里不当成"打包行"。
            return null;
        }
        return null;
    }

    private static IReadOnlyList<SessionEvent> ExpandRow(ReadOnlySpan<byte> line, string tag)
    {
        var isToolCall = tag == ToolCallChunksTag;
        var seq0 = -1L;
        var time0 = 0L;
        var turn = 0;
        var step = 0;
        var index = 0;
        string? callId = null;
        string? callName = null;
        long[]? gaps = null;
        List<string>? members = null;
        var keys = 0;
        try
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw new FormatException("stored session records must be objects");
            var sawType = false;
            var sawSeq0 = false;
            var sawTime0 = false;
            var sawData = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                keys += 1;
                if (reader.ValueTextEquals("type"u8))
                {
                    sawType = true;
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals(tag))
                        throw Malformed(tag, $"type must be \"{tag}\"");
                }
                else if (reader.ValueTextEquals("seq0"u8))
                {
                    sawSeq0 = true;
                    reader.Read();
                    seq0 = ReadSafeInt64(ref reader, tag, "seq0");
                    if (seq0 < 0) throw Malformed(tag, "seq0 must be a non-negative safe integer");
                }
                else if (reader.ValueTextEquals("time0"u8))
                {
                    sawTime0 = true;
                    reader.Read();
                    time0 = ReadSafeInt64(ref reader, tag, "time0");
                }
                else if (reader.ValueTextEquals("data"u8))
                {
                    sawData = true;
                    reader.Read();
                    ReadRowData(ref reader, tag, isToolCall, out turn, out step, out index, out callId, out callName, out gaps, out members);
                }
                else
                {
                    throw Malformed(tag, "envelope must be exactly {type, seq0, time0, data}");
                }
            }
            if (keys != 4 || !sawType || !sawSeq0 || !sawTime0 || !sawData)
                throw Malformed(tag, "envelope must be exactly {type, seq0, time0, data}");
        }
        catch (JsonException error)
        {
            throw new FormatException("corrupt session log: stored record is not valid JSON", error);
        }
        if (members is null || gaps is null) throw Malformed(tag, "row data is missing");
        if (gaps.Length != members.Count - 1)
            throw Malformed(tag, $"dt length {gaps.Length} does not match {members.Count} members");
        if (members.Count - 1 > long.MaxValue - seq0) throw Malformed(tag, "member seqs must stay safe integers");
        var events = new List<SessionEvent>(members.Count);
        var time = time0;
        for (var k = 0; k < members.Count; k += 1)
        {
            if (k > 0)
            {
                try { time = checked(time + gaps[k - 1]); }
                catch (OverflowException) { throw Malformed(tag, "member times must stay safe integers"); }
            }
            StreamChunk chunk = tag switch
            {
                TextChunksTag => new StreamChunk.TextDelta(index, members[k]),
                ReasoningChunksTag => new StreamChunk.ReasoningDelta(index, members[k]),
                _ => new StreamChunk.ToolCallDelta(index, ToolCallId.Create(callId!), callName, members[k]),
            };
            events.Add(new SessionEvent
            {
                Type = SessionEventTypes.AssistantChunk,
                Seq = checked(seq0 + k),
                Time = time,
                Data = new AssistantChunkPayload(turn, step, chunk),
            });
        }
        return events;
    }

    /** 打包行的 data 对象: {turn, step, index, dt, texts} 或 {turn, step, index, id, name?, dt, args}。 */
    private static void ReadRowData(
        ref Utf8JsonReader reader,
        string tag,
        bool isToolCall,
        out int turn,
        out int step,
        out int index,
        out string? callId,
        out string? callName,
        out long[]? gaps,
        out List<string>? members)
    {
        var payloadKey = isToolCall ? "args" : "texts";
        if (reader.TokenType != JsonTokenType.StartObject) throw Malformed(tag, "data must be an object");
        turn = 0;
        step = 0;
        index = 0;
        callId = null;
        callName = null;
        gaps = null;
        members = null;
        var keys = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            keys += 1;
            if (reader.ValueTextEquals("turn"u8))
            {
                reader.Read();
                turn = ReadSafeInt32(ref reader, tag, "turn");
            }
            else if (reader.ValueTextEquals("step"u8))
            {
                reader.Read();
                step = ReadSafeInt32(ref reader, tag, "step");
            }
            else if (reader.ValueTextEquals("index"u8))
            {
                reader.Read();
                index = ReadSafeInt32(ref reader, tag, "index");
            }
            else if (isToolCall && reader.ValueTextEquals("id"u8))
            {
                reader.Read();
                callId = ReadString(ref reader, tag);
            }
            else if (isToolCall && reader.ValueTextEquals("name"u8))
            {
                reader.Read();
                callName = ReadString(ref reader, tag);
            }
            else if (reader.ValueTextEquals("dt"u8))
            {
                reader.Read();
                gaps = ReadGaps(ref reader, tag);
            }
            else if (reader.ValueTextEquals(payloadKey))
            {
                reader.Read();
                members = ReadMembers(ref reader, tag, payloadKey);
            }
            else
            {
                throw Malformed(tag, isToolCall
                    ? "data must be exactly {turn, step, index, id, name?, dt, args}"
                    : "data must be exactly {turn, step, index, dt, texts}");
            }
        }
        var expected = isToolCall ? new[] { "turn", "step", "index", "id", "dt", payloadKey } : ["turn", "step", "index", "dt", payloadKey];
        if (keys < expected.Length || keys > expected.Length + 1 || members is null || gaps is null || (isToolCall && callId is null))
            throw Malformed(tag, isToolCall
                ? "data must be exactly {turn, step, index, id, name?, dt, args}"
                : "data must be exactly {turn, step, index, dt, texts}");
    }

    private static long ReadSafeInt64(ref Utf8JsonReader reader, string tag, string field)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value))
            throw Malformed(tag, $"{field} must be a safe integer");
        return value;
    }

    private static int ReadSafeInt32(ref Utf8JsonReader reader, string tag, string field)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var value))
            throw Malformed(tag, $"{field} must be a safe integer");
        return value;
    }

    private static string ReadString(ref Utf8JsonReader reader, string tag)
    {
        if (reader.TokenType != JsonTokenType.String) throw Malformed(tag, "id (and name when present) must be strings");
        return reader.GetString() ?? throw Malformed(tag, "id (and name when present) must be strings");
    }

    private static long[] ReadGaps(ref Utf8JsonReader reader, string tag)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Malformed(tag, "dt must be an array of safe integers");
        var values = new List<long>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            values.Add(ReadSafeInt64(ref reader, tag, "dt"));
        return [.. values];
    }

    private static List<string> ReadMembers(ref Utf8JsonReader reader, string tag, string payloadKey)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Malformed(tag, $"{payloadKey} must be a non-empty string array");
        var values = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String) throw Malformed(tag, $"{payloadKey} must be a non-empty string array");
            values.Add(reader.GetString() ?? throw Malformed(tag, $"{payloadKey} must be a non-empty string array"));
        }
        if (values.Count == 0) throw Malformed(tag, $"{payloadKey} must be a non-empty string array");
        return values;
    }

    private static FormatException Malformed(string tag, string why)
        => new($"malformed {tag} storage row: {why}");
}
