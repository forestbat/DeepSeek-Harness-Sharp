using System.Collections.Concurrent;
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
    private readonly record struct Registration(
        Func<JsonElement, JsonSerializerOptions, SessionEventPayload> Reader,
        Action<SessionEventPayload, Utf8JsonWriter, JsonSerializerOptions> Writer,
        bool Ignorable);

    private static readonly ConcurrentDictionary<string, Registration> Registrations = new(StringComparer.Ordinal);

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
        // 未知该类型的旧版本跳过它即可(不隐藏任何工具), 所以可以标注 ignorable。
        Register<ToolDisclosedPayload>(SessionEventTypes.ToolDisclosed, ignorable: true);
        Register<FileChangedPayload>(SessionEventTypes.FileChanged, ignorable: true);
        Register<SettingsMutationAuditPayload>(SettingsMutationAuditPayload.EventType);
        Register<RequestHeaderPayload>(SessionEventTypes.RequestHeader);
        Register<SystemMessagePayload>(SessionEventTypes.SystemMessage);
        Register<RequestContextPayload>(SessionEventTypes.RequestContext);
        Register<SessionEndSeedPayload>(SessionEventTypes.SessionEndSeed);
        Register<InboxSplicePayload>(SessionEventTypes.AgentInboxSpliced);
        Register<WorkingDirectoryChangedPayload>(SessionEventTypes.WorkingDirectoryChange);
        Register<UserMessagePayload>(SessionEventTypes.UserMessage,
            (element, options) => new UserMessagePayload(
                DshJson.Deserialize<UserMessage>(element)
                ?? throw new JsonException("user/message payload is not a user message")),
            (payload, writer, options) => DshJson.Serialize(writer, payload.Message));
    }

    public static void Register<T>(string type, bool ignorable = false) where T : SessionEventPayload
        => Register<T>(type,
            (element, options) => DshJson.Deserialize<T>(element)
                ?? throw new JsonException($"invalid \"{type}\" payload"),
            static (payload, writer, options) => DshJson.Serialize(writer, payload),
            ignorable);

    /**
     * ignorable: 该类型的事件写盘时带 `ignorable:true`, 不认识该类型的版本读到时跳过它而不是拒开整份日志。
     * 只适用于不进入模型上下文、不改变折叠语义的事件; surface 事件被跳过会让旧版本拼出不同的上下文, 因此不允许。
     */
    public static void Register<T>(
        string type,
        Func<JsonElement, JsonSerializerOptions, T> reader,
        Action<T, Utf8JsonWriter, JsonSerializerOptions> writer,
        bool ignorable = false) where T : SessionEventPayload
    {
        if (ignorable && SessionEventTypes.SurfaceEligible.Contains(type))
            throw new ArgumentException($"surface event type \"{type}\" cannot be registered as ignorable", nameof(ignorable));
        Registrations[type] = new Registration(
            reader,
            (payload, w, options) => writer((T)payload, w, options),
            ignorable);
    }

    public static bool IsRegistered(string type) => Registrations.ContainsKey(type);

    public static bool IsIgnorable(string type) => Registrations.TryGetValue(type, out var registration) && registration.Ignorable;

    public static SessionEventPayload ReadPayload(string type, JsonElement data, bool ignorable, JsonSerializerOptions options)
    {
        if (Registrations.TryGetValue(type, out var registration))
            return registration.Reader(data, options);
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
        if (!Registrations.TryGetValue(payload.Type, out var registration))
            throw new JsonException($"no session payload writer registered for \"{payload.Type}\"");
        registration.Writer(payload, writer, options);
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
                ? DshJson.Deserialize<SurfaceOp>(surfaceOp)
                : null,
            SourceEventSeqs = root.TryGetProperty("sourceEventSeqs", out var seqs)
                ? DshJson.Deserialize<IReadOnlyList<long>>(seqs)
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
            DshJson.Serialize(writer, surfaceOp);
        }
        if (value.SourceEventSeqs is { } sourceEventSeqs)
        {
            writer.WritePropertyName("sourceEventSeqs");
            DshJson.Serialize(writer, sourceEventSeqs);
        }
        writer.WriteEndObject();
    }
}
