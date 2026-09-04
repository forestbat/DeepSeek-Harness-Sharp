using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Llm;

namespace Dsh.Core;

[JsonConverter(typeof(SessionEventJsonConverter))]
public sealed record SessionEvent
{
    public required string Type { get; init; }
    public required long Seq { get; init; }
    public required long Time { get; init; }
    public required SessionEventPayload Data { get; init; }
    public bool Ignorable { get; init; }
    public SurfaceOp? SurfaceOp { get; init; }
    public IReadOnlyList<long>? SourceEventSeqs { get; init; }

    public bool IsSurfaceEligible => SessionEventTypes.SurfaceEligible.Contains(Type);
}

public static class SessionEventCodec
{
    private static readonly Dictionary<string, Func<JsonElement, JsonSerializerOptions, SessionEventPayload>> Readers = [];
    private static readonly Dictionary<string, Action<SessionEventPayload, Utf8JsonWriter, JsonSerializerOptions>> Writers = [];

    static SessionEventCodec()
    {
        Register<TurnStartPayload>(SessionEventTypes.TurnStart);
        Register<TurnEndPayload>(SessionEventTypes.TurnEnd);
        Register<StepStartPayload>(SessionEventTypes.StepStart);
        Register<StepEndPayload>(SessionEventTypes.StepEnd);
        Register<AssistantChunkPayload>(SessionEventTypes.AssistantChunk);
        Register<AssistantMessagePayload>(SessionEventTypes.AssistantMessage);
        Register<ToolCallPayload>(SessionEventTypes.ToolCall);
        Register<ToolResultPayload>(SessionEventTypes.ToolResult);
        Register<RequestHeaderPayload>(SessionEventTypes.RequestHeader);
        Register<RequestContextPayload>(SessionEventTypes.RequestContext);
        Register<SessionEndSeedPayload>(SessionEventTypes.SessionEndSeed);
        Register<InboxSplicePayload>(SessionEventTypes.AgentInboxSpliced);
        Register<UserMessagePayload>(SessionEventTypes.UserMessage,
            (element, options) => new UserMessagePayload(
                element.Deserialize<UserMessage>(options)
                ?? throw new JsonException("user/message payload is not a user message")),
            (payload, writer, options) => JsonSerializer.Serialize(writer, ((UserMessagePayload)payload).Message, options));
    }

    public static void Register<T>(string type) where T : SessionEventPayload
        => Register<T>(type,
            (element, options) => element.Deserialize<T>(options)
                ?? throw new JsonException($"invalid \"{type}\" payload"),
            static (payload, writer, options) => JsonSerializer.Serialize(writer, (T)payload, options));

    public static void Register<T>(
        string type,
        Func<JsonElement, JsonSerializerOptions, T> reader,
        Action<T, Utf8JsonWriter, JsonSerializerOptions> writer) where T : SessionEventPayload
    {
        Readers[type] = (element, options) => reader(element, options);
        Writers[type] = (payload, w, options) => writer((T)payload, w, options);
    }

    public static bool IsRegistered(string type) => Readers.ContainsKey(type);

    public static SessionEventPayload ReadPayload(string type, JsonElement data, bool ignorable, JsonSerializerOptions options)
    {
        if (Readers.TryGetValue(type, out var reader))
            return reader(data, options);
        if (ignorable)
            return new UnknownSessionEventPayload(type, data.Clone());
        throw new JsonException($"unrecognized session event type \"{type}\" without the ignorable marker");
    }

    public static void WritePayload(SessionEventPayload payload, Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        if (payload is UnknownSessionEventPayload unknown)
        {
            unknown.Raw.WriteTo(writer);
            return;
        }
        if (!Writers.TryGetValue(payload.Type, out var write))
            throw new JsonException($"no session payload writer registered for \"{payload.Type}\"");
        write(payload, writer, options);
    }
}

public sealed class SessionEventJsonConverter : JsonConverter<SessionEvent>
{
    private static readonly HashSet<string> EnvelopeKeys = ["type", "seq", "time", "data", "surfaceOp", "sourceEventSeqs", "ignorable"];

    public override SessionEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        foreach (var property in root.EnumerateObject())
        {
            if (!EnvelopeKeys.Contains(property.Name))
                throw new JsonException("session event has an invalid event envelope");
        }
        var type = root.GetProperty("type").GetString() ?? throw new JsonException("session event missing type");
        if (type == "request/header-delta")
            throw new JsonException("session event uses unsupported legacy request/header-delta format");
        var seq = root.GetProperty("seq").GetInt64();
        var time = root.GetProperty("time").GetInt64();
        if (seq < 0)
            throw new JsonException("session event has an invalid event envelope");
        var ignorable = root.TryGetProperty("ignorable", out var ignorableElement) && ignorableElement.GetBoolean();
        var data = SessionEventCodec.ReadPayload(type, root.GetProperty("data"), ignorable, options);
        if (type == SessionEventTypes.RequestHeader
            && data is RequestHeaderPayload { Reason: "fallback" })
        {
            throw new JsonException("session event uses unsupported legacy request/header reason \"fallback\"");
        }
        return new SessionEvent
        {
            Type = type,
            Seq = seq,
            Time = time,
            Data = data,
            Ignorable = ignorable,
            SurfaceOp = root.TryGetProperty("surfaceOp", out var surfaceOp)
                ? surfaceOp.Deserialize<SurfaceOp>(options)
                : null,
            SourceEventSeqs = root.TryGetProperty("sourceEventSeqs", out var seqs)
                ? seqs.Deserialize<IReadOnlyList<long>>(options)
                : null,
        };
    }

    public override void Write(Utf8JsonWriter writer, SessionEvent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        writer.WriteNumber("seq", value.Seq);
        writer.WriteNumber("time", value.Time);
        writer.WritePropertyName("data");
        SessionEventCodec.WritePayload(value.Data, writer, options);
        if (value.Ignorable)
            writer.WriteBoolean("ignorable", true);
        if (value.SurfaceOp is { } surfaceOp)
        {
            writer.WritePropertyName("surfaceOp");
            JsonSerializer.Serialize(writer, surfaceOp, options);
        }
        if (value.SourceEventSeqs is { } sourceEventSeqs)
        {
            writer.WritePropertyName("sourceEventSeqs");
            JsonSerializer.Serialize(writer, sourceEventSeqs, options);
        }
        writer.WriteEndObject();
    }
}
