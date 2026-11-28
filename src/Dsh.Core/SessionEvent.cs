using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Llm;

namespace Dsh.Core;

[JsonConverter(typeof(SessionEventJsonConverter))]
public sealed record SessionEvent
{
    private SessionEventPayload? _data;
    private ReadOnlyMemory<byte> _rawRecord;
    private string? _type;
    private long? _time;
    /** 只读信封时的延迟来源: 真正要 Type/Time/Data 了才去解那一条记录。 */
    private Func<SessionEvent>? _deferred;
    private SessionEvent? _resolved;

    public SessionEvent()
    {
    }

    /**
     * 惰性负载的构造入口: 只给信封, `data` 留到真正访问 Data 时再从原始记录里解出来。
     * 原始记录指向"解码时那一帧的明文数组", 一帧的记录共用同一个数组, 所以每条事件不额外分配。
     */
    [SetsRequiredMembers]
#pragma warning disable CS8618 // Data 由 getter 从原始记录物化, 构造时有意不填
    internal SessionEvent(string type, long seq, long time, bool ignorable, SurfaceOp? surfaceOp,
        IReadOnlyList<long>? sourceEventSeqs, ReadOnlyMemory<byte> rawRecord)
#pragma warning restore CS8618
    {
        Type = type;
        Seq = seq;
        Time = time;
        Ignorable = ignorable;
        SurfaceOp = surfaceOp;
        SourceEventSeqs = sourceEventSeqs;
        _rawRecord = rawRecord;
    }

    /**
     * **只读信封**的构造入口: 序号立刻可用(索引里就有), 类型/时刻/负载留到第一次真正用到时才解。
     *
     * 用于"百万条事件从头扫到尾"这类动作: 扫的时候只取序号, 一条负载都不解, 于是耗时从"整段解码"
     * 降到"查表"; 只有视口里真正要渲染的那几条才会解出负载。解出来的负载由 `resolve` 自己缓存。
     */
[SetsRequiredMembers]
#pragma warning disable CS8618 // Type/Time/Data 由 _deferred 提供
    internal SessionEvent(long seq, Func<SessionEvent> resolve)
#pragma warning restore CS8618
    {
        Seq = seq;
        _deferred = resolve;
    }

    private SessionEvent Resolved => _resolved ??= _deferred!();

    /** 只读信封事件的构造入口: 序号立刻可用, 类型/时刻/负载第一次用到时才由 `resolve` 解出。 */
    public static SessionEvent Deferred(long seq, Func<SessionEvent> resolve) => new(seq, resolve);

    /** 事件类型。只读信封时由 `_deferred` 解出(第一次访问才解)。 */
    public required string Type
    {
        get => _type ?? Resolved.Type;
        init => _type = value;
    }

    /** 序号: 索引里直接就有, 不触发任何解码。 */
    public required long Seq { get; init; }

    /** 时刻(Unix 毫秒)。只读信封时由 `_deferred` 解出。 */
    public required long Time
    {
        get => _time ?? Resolved.Time;
        init => _time = value;
    }

    /**
     * 负载。窗口读取时它是惰性的: 切窗与折叠只用得到信封, 把 2 万条事件的 payload 全物化是白花的。
     * 全量读取不走惰性 —— 那条路径要把整份日志留在内存里, 逐条留原始记录等于明文再存一份。
     */
    public required SessionEventPayload Data
    {
        get
        {
            if (_data is { } cached)
                return cached;
            var payload = _deferred is not null ? Resolved.Data : SessionEventCodec.DecodeRecordPayload(_rawRecord);
            // 解出来就不再压着那一帧明文: 否则整帧数组会被单条消息一直吊着。
            _rawRecord = default;
            return _data = payload;
        }
        init => _data = value;
    }

    public bool Ignorable { get; init; }
    public SurfaceOp? SurfaceOp { get; init; }
    public IReadOnlyList<long>? SourceEventSeqs { get; init; }

    public bool IsSurfaceEligible => SessionEventTypes.SurfaceEligible.Contains(Type);

    /**
     * 记录生成的相等性会比较所有字段, 包括惰性负载的缓存状态 —— 那样"同一条记录, 一条已物化一条没有"就不相等了。
     * 相等性只认信封与已解出的负载。
     */
    public bool Equals(SessionEvent? other)
        => other is not null
            && Type == other.Type
            && Seq == other.Seq
            && Time == other.Time
            && Ignorable == other.Ignorable
            && Equals(SurfaceOp, other.SurfaceOp)
            && (SourceEventSeqs ?? []).SequenceEqual(other.SourceEventSeqs ?? [])
            && Equals(Data, other.Data);

    public override int GetHashCode() => HashCode.Combine(Type, Seq, Time, Ignorable);
}

public static class SessionEventCodec
{
    private readonly record struct Registration(
        Func<JsonElement, JsonSerializerOptions, SessionEventPayload> Reader,
        Action<SessionEventPayload, Utf8JsonWriter, JsonSerializerOptions> Writer,
        bool Ignorable);

    private static readonly ConcurrentDictionary<string, Registration> Registrations = new(StringComparer.Ordinal);

    /** ref struct 不能作为泛型实参, 所以这里用专门的委托类型而不是 Action<...>。 */
    private delegate SessionEventPayload PayloadReader(ref Utf8JsonReader reader, JsonSerializerOptions options);

    /**
     * 类型 -> 直接从 reader 读 payload 的分支。
     * data 的结构由 type 决定, 所以只能先读到 type; 而"读完 type 再把 data 建成 JsonElement 再去反序列化"
     * 等于把同一段数据解析两遍 —— 这条表让常见类型只解析一遍。
     * 注册表之外的(含插件自定义与旧版本未知类型)仍走 JsonElement 路径。
     */
    private static readonly ConcurrentDictionary<string, PayloadReader> Readers = new(StringComparer.Ordinal);

    /** 注册表里每个类型标签的 UTF-8 字节: 逐行判断"是不是已知类型"时不必为每行分配一个 string。 */
    private static volatile byte[][] _tags = [];

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
        // 回退不进 ignorable: 旧版本跳过它就会把已经回退的内容继续当上下文发给模型。
        Register<SessionRevertPayload>(SessionEventTypes.SessionRevert);
        Register<InboxSplicePayload>(SessionEventTypes.AgentInboxSpliced);
        Register<AgentEnvelopePayload>(SessionEventTypes.AgentEnvelope, ignorable: true);
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
        Readers[type] = static (ref Utf8JsonReader reader, JsonSerializerOptions _) =>
            JsonSerializer.Deserialize(ref reader, DshJson.Info<T>())
                ?? throw new JsonException("invalid session payload");
        RefreshTags();
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
        RefreshTags();
    }

    private static void RefreshTags()
        => _tags = [.. Registrations.Keys.Select(Encoding.UTF8.GetBytes)];

    public static bool IsRegistered(string type) => Registrations.ContainsKey(type);

    /** 已知类型判定不分配: 逐行解析时每行都会问一次, 为它解码一个 string 是纯开销。 */
    public static bool IsRegisteredUtf8(ReadOnlySpan<byte> utf8Type)
    {
        foreach (var tag in _tags)
            if (utf8Type.SequenceEqual(tag))
                return true;
        // 快照里没有: 可能是真的不认识的类型, 也可能只是刚注册还没进快照, 退回按 string 查。
        return Registrations.ContainsKey(Encoding.UTF8.GetString(utf8Type));
    }

    public static bool IsIgnorable(string type) => Registrations.TryGetValue(type, out var registration) && registration.Ignorable;

    /**
     * 直接从 reader 读 data。返回 false 表示这个类型没有直读分支(插件自定义类型等), 调用方需退回 JsonElement 路径。
     * reader 必须正停在 data 的值起始处。
     */
    public static bool TryReadDirectly(string type, ref Utf8JsonReader reader, JsonSerializerOptions options, out SessionEventPayload payload)
    {
        if (Readers.TryGetValue(type, out var read))
        {
            payload = read(ref reader, options);
            return true;
        }
        payload = null!;
        return false;
    }

    /**
     * 惰性负载的物化: 把整条记录再解一遍, 取它的 payload。
     * 复用完整解码器而不是另写一个只解 data 的分支 —— 未知类型、旧格式拒绝、通道校验都在那一条路上, 分叉就会走偏。
     */
    internal static SessionEventPayload DecodeRecordPayload(ReadOnlyMemory<byte> record)
    {
        if (record.IsEmpty)
            throw new JsonException("session event has no payload source");
        var decoded = JsonSerializer.Deserialize(record.Span, DshJson.Info<SessionEvent>())
            ?? throw new JsonException("stored session record deserialized to null");
        return decoded.Data;
    }

    public static SessionEventPayload ReadPayload(string type, JsonElement data, bool ignorable, JsonSerializerOptions options)
    {
        if (Registrations.TryGetValue(type, out var registration))
            return registration.Reader(data, options);
        // 本 harness 不认识的类型一律按**不透明记录**保留, 不再按 ignorable 标记决定读不读得下来:
        // 丢掉它们会逼着读侧重排序号, 而序号一重排, sourceEventSeqs 与 surfaceOp.start 指向的节点就全错位
        // (实测让尾窗一路退到文件开头, resume 从 0.3 s 变 3 s)。原样保留则序号与指向关系都成立。
        _ = ignorable;
        return new UnknownSessionEventPayload(type, data.Clone());
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
                // 存盘形态是区间编码([start,end] 或数字), 不是裸数组: 在这里就地展开,
                // 于是带来源列表的记录也不必先建成 JsonNode 再反序列化一遍。
                sourceEventSeqs = SeqRanges.Decode(ref reader, seq >= 0 ? seq : long.MaxValue);
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
