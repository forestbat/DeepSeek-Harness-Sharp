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

    /** ref struct 不能作为泛型实参, 所以这里用专门的委托类型而不是 Action<...>。 */
    private delegate void PayloadReader(ref Utf8JsonReader reader, JsonSerializerOptions options, PayloadSlot slot);

    /**
     * 类型 -> 直接从 reader 读 payload 的分支。
     * data 的结构由 type 决定, 所以只能先读到 type; 而"读完 type 再把 data 建成 JsonElement 再去反序列化"
     * 等于把同一段数据解析两遍 —— 这条表让常见类型只解析一遍。
     * 注册表之外的(含插件自定义与旧版本未知类型)仍走 JsonElement 路径。
     */
    private static readonly ConcurrentDictionary<string, PayloadReader> Readers = new(StringComparer.Ordinal);

    /** 直接读取的结果容器: 用可变槽位把 payload 带出 ref struct 调用。 */
    public sealed class PayloadSlot
    {
        public SessionEventPayload? Value { get; set; }
    }

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
        Register<SafetyDecisionAuditPayload>(SafetyDecisionAuditPayload.EventType, ignorable: true);
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

    /** 常见 payload 既能从 reader 直接读, 也保留 JsonElement 路径给未知/插件类型。 */
    public static void Register<T>(string type, bool ignorable = false) where T : SessionEventPayload
    {
        Register<T>(type,
            (element, options) => DshJson.Deserialize<T>(element)
                ?? throw new JsonException($"invalid \"{type}\" payload"),
            static (payload, writer, options) => DshJson.Serialize(writer, payload),
            ignorable);
        // payload 类型由 DshJson 的全局解析器提供; 传入的 options 只覆盖会话外壳那几个类型。
        Readers[type] = static (ref Utf8JsonReader reader, JsonSerializerOptions options, PayloadSlot slot) =>
            slot.Value = JsonSerializer.Deserialize(ref reader, DshJson.Info<T>())
                ?? throw new JsonException("invalid session payload");
    }

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

    public static bool IsRegistered(string type) => Registrations.ContainsKey(type);    public static bool IsIgnorable(string type) => Registrations.TryGetValue(type, out var registration) && registration.Ignorable;

    /**
     * 直接从 reader 读 data。返回 false 表示这个类型没有直读分支(插件自定义类型等), 调用方需退回 JsonElement 路径。
     * reader 必须正停在 data 的值起始处。
     */
    public static bool TryReadDirectly(string type, ref Utf8JsonReader reader, JsonSerializerOptions options, out SessionEventPayload payload)
    {
        if (Readers.TryGetValue(type, out var read))
        {
            var slot = new PayloadSlot();
            read(ref reader, options, slot);
            payload = slot.Value ?? throw new JsonException($"invalid \"{type}\" payload");
            return true;
        }
        payload = null!;
        return false;
    }

    public static SessionEventPayload ReadPayload(string type, JsonElement data, bool ignorable, JsonSerializerOptions options)
    {        if (Registrations.TryGetValue(type, out var registration))
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
    public override SessionEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("session event must be an object");
        string? type = null;
        long seq = -1;
        long time = 0;
        var ignorable = false;
        JsonElement data = default;
        var hasData = false;
        SessionEventPayload? payload = null;
        var hasPayload = false;
        SurfaceOp? surfaceOp = null;
        IReadOnlyList<long>? sourceEventSeqs = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("session event has an invalid event envelope");
            if (reader.ValueTextEquals("type"))
            {
                reader.Read();
                type = reader.GetString();
                continue;
            }
            if (reader.ValueTextEquals("seq"))
            {
                reader.Read();
                seq = reader.GetInt64();
                continue;
            }
            if (reader.ValueTextEquals("time"))
            {
                reader.Read();
                time = reader.GetInt64();
                continue;
            }
            if (reader.ValueTextEquals("ignorable"))
            {
                reader.Read();
                ignorable = reader.GetBoolean();
                continue;
            }
            if (reader.ValueTextEquals("surfaceOp"))
            {
                reader.Read();
                surfaceOp = DshJson.Deserialize<SurfaceOp>(JsonElement.ParseValue(ref reader));
                continue;
            }
            if (reader.ValueTextEquals("sourceEventSeqs"))
            {
                reader.Read();
                sourceEventSeqs = DshJson.Deserialize<IReadOnlyList<long>>(JsonElement.ParseValue(ref reader));
                continue;
            }
            if (reader.ValueTextEquals("data"))
            {
                reader.Read();
                // type 已在前就直读, 免掉"建 JsonElement 再反序列化"的第二遍解析;
                // data 排在 type 之前(旧日志或手写 JSON 都可能这样)时无法分派, 只能先留住。
                if (type is not null && SessionEventCodec.TryReadDirectly(type, ref reader, options, out var direct))
                {
                    payload = direct;
                    hasPayload = true;
                }
                else
                {
                    data = JsonElement.ParseValue(ref reader);
                    hasData = true;
                }
                continue;
            }
            throw new JsonException("session event has an invalid event envelope");
        }
        if (type is null)
            throw new JsonException("session event missing type");
        if (type == "request/header-delta")
            throw new JsonException("session event uses unsupported legacy request/header-delta format");
        if (seq < 0 || (!hasPayload && !hasData))
            throw new JsonException("session event has an invalid event envelope");
        payload ??= SessionEventCodec.ReadPayload(type, data, ignorable, options);
        if (type == SessionEventTypes.RequestHeader
            && payload is RequestHeaderPayload { Reason: "fallback" })
        {
            throw new JsonException("session event uses unsupported legacy request/header reason \"fallback\"");
        }
        return new SessionEvent
        {
            Type = type,
            Seq = seq,
            Time = time,
            Data = payload,
            Ignorable = ignorable,
            SurfaceOp = surfaceOp,
            SourceEventSeqs = sourceEventSeqs,
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
