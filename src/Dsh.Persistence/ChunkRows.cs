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

    public static bool IsChunkRowTag(string? type)
        => type is TextChunksTag or ReasoningChunksTag or ToolCallChunksTag;

    public static string EncodeEventLines(IReadOnlyList<SessionEvent> events, bool packChunks)
    {
        var records = packChunks ? PackRuns(events) : events.Cast<object>().ToList();
        var lines = new List<string>(records.Count);
        foreach (var record in records)
            lines.Add(record is PackedChunkRow row ? SerializeRow(row) : SerializeEvent((SessionEvent)record));
        return string.Join('\n', lines);
    }

    public static IReadOnlyList<SessionEvent> DecodeStorageRecord(JsonObject record)
    {
        ExpandProvenance(record);
        var type = ReadTypeTag(record);
        if (IsChunkRowTag(type)) return ExpandRow(record, type!);
        if (type is not null && !SessionEventCodec.IsRegistered(type) && !IsIgnorable(record))
            throw new SessionFormatUnsupportedException(
                $"session log contains event type \"{type}\" unknown to this harness and not marked ignorable; refusing to interpret the log — it was likely written by a newer harness");
        if (type == SessionEventTypes.RequestHeader
            && record["data"] is JsonObject data
            && data["reason"] is JsonValue reasonValue
            && reasonValue.TryGetValue<string>(out var reason)
            && reason == "fallback")
        {
            throw new SessionFormatUnsupportedException(
                "session log contains a request/header event with the unsupported legacy reason \"fallback\"; refusing to interpret the log — it was written by a retired pre-release harness");
        }
        return [record.Deserialize<SessionEvent>(DshJson.Options)
            ?? throw new FormatException("stored session record deserialized to null")];
    }

    private static string? ReadTypeTag(JsonObject record)
        => record["type"] is JsonValue value && value.TryGetValue<string>(out var type) ? type : null;

    private static bool IsIgnorable(JsonObject record)
        => record["ignorable"] is JsonValue value && value.TryGetValue<bool>(out var ignorable) && ignorable;

    private static void ExpandProvenance(JsonObject record)
    {
        if (record["sourceEventSeqs"] is not { } stored) return;
        if (record["seq"] is not JsonValue seqValue || !seqValue.TryGetValue<long>(out var seq) || seq < 0)
            throw new FormatException("stored session event seq must be a non-negative safe integer");
        var expanded = new JsonArray();
        foreach (var seqEntry in SeqRanges.Decode(stored, seq)) expanded.Add(seqEntry);
        record["sourceEventSeqs"] = expanded;
    }

    private static string SerializeEvent(SessionEvent sessionEvent)
    {
        var node = JsonSerializer.SerializeToNode(sessionEvent, DshJson.Options)!.AsObject();
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

    private static IReadOnlyList<SessionEvent> ExpandRow(JsonObject record, string tag)
    {
        if (!HasExactKeys(record, "type", "seq0", "time0", "data"))
            throw Malformed(tag, "envelope must be exactly {type, seq0, time0, data}");
        var seq0 = ReadInt64(record["seq0"], tag, "seq0");
        if (seq0 < 0) throw Malformed(tag, "seq0 must be a non-negative safe integer");
        var time0 = ReadInt64(record["time0"], tag, "time0");
        if (record["data"] is not JsonObject data) throw Malformed(tag, "data must be an object");
        var isToolCall = tag == ToolCallChunksTag;
        var keysOk = isToolCall
            ? HasExactKeys(data, "turn", "step", "index", "id", "dt", "args")
              || HasExactKeys(data, "turn", "step", "index", "id", "name", "dt", "args")
            : HasExactKeys(data, "turn", "step", "index", "dt", "texts");
        if (!keysOk)
            throw Malformed(tag, isToolCall
                ? "data must be exactly {turn, step, index, id, name?, dt, args}"
                : "data must be exactly {turn, step, index, dt, texts}");
        var turn = ReadInt32(data["turn"], tag, "turn");
        var step = ReadInt32(data["step"], tag, "step");
        var index = ReadInt32(data["index"], tag, "index");
        string? callId = null;
        string? callName = null;
        if (isToolCall)
        {
            if (data["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out callId))
                throw Malformed(tag, "id (and name when present) must be strings");
            if (data["name"] is { } nameNode
                && (nameNode is not JsonValue nameValue || !nameValue.TryGetValue<string>(out callName)))
                throw Malformed(tag, "id (and name when present) must be strings");
        }
        var payloadKey = isToolCall ? "args" : "texts";
        if (data[payloadKey] is not JsonArray payload || payload.Count == 0)
            throw Malformed(tag, $"{payloadKey} must be a non-empty string array");
        var members = new string[payload.Count];
        for (var k = 0; k < payload.Count; k += 1)
        {
            if (payload[k] is not JsonValue memberValue || !memberValue.TryGetValue<string>(out members[k]!))
                throw Malformed(tag, $"{payloadKey} must be a non-empty string array");
        }
        if (data["dt"] is not JsonArray dtNode) throw Malformed(tag, "dt must be an array of safe integers");
        if (dtNode.Count != members.Length - 1)
            throw Malformed(tag, $"dt length {dtNode.Count} does not match {members.Length} members");
        var dt = new long[dtNode.Count];
        for (var k = 0; k < dtNode.Count; k += 1) dt[k] = ReadInt64(dtNode[k], tag, "dt");
        if (members.Length - 1 > long.MaxValue - seq0) throw Malformed(tag, "member seqs must stay safe integers");
        var events = new List<SessionEvent>(members.Length);
        var time = time0;
        for (var k = 0; k < members.Length; k += 1)
        {
            if (k > 0)
            {
                try { time = checked(time + dt[k - 1]); }
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

    private static bool HasExactKeys(JsonObject value, params string[] keys)
    {
        if (value.Count != keys.Length) return false;
        foreach (var key in keys)
            if (!value.ContainsKey(key)) return false;
        return true;
    }

    private static long ReadInt64(JsonNode? node, string tag, string field)
    {
        if (node is JsonValue value && value.TryGetValue<long>(out var result)) return result;
        throw Malformed(tag, $"{field} must be a safe integer");
    }

    private static int ReadInt32(JsonNode? node, string tag, string field)
    {
        if (node is JsonValue value && value.TryGetValue<int>(out var result)) return result;
        throw Malformed(tag, $"{field} must be a safe integer");
    }

    private static FormatException Malformed(string tag, string why)
        => new($"malformed {tag} storage row: {why}");
}
