using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Schedule;

namespace Dsh.Tests;

/**
 * 新写路径写出的日志交给不认识插件事件类型的版本读取。
 * 读取方在独立的 AssemblyLoadContext 里重新装载 Dsh.* 程序集，其静态 codec 表只有核心类型，等同于没装该插件的旧版本。
 */
public sealed class SessionLogCompatibilityTests : IDisposable
{
    private const string IgnorableType = "compat-probe/annotation";
    private const string RequiredType = "compat-probe/required";
    private const string UnknownPayloadName = nameof(UnknownSessionEventPayload);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dsh-compat-{Guid.NewGuid():N}");

    static SessionLogCompatibilityTests()
    {
        ProbePayload.Register(IgnorableType, ignorable: true);
        ProbePayload.Register(RequiredType, ignorable: false);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Append_MarksOnlyTypesRegisteredAsIgnorable()
    {
        var session = Session.Create(SessionId.Create(Guid.NewGuid().ToString("N")));

        Assert.False(session.Append(new TurnStartPayload(1)).Ignorable);
        Assert.True(session.Append(new ProbePayload(IgnorableType, "note")).Ignorable);
        Assert.False(session.Append(new ProbePayload(RequiredType, "note")).Ignorable);
    }

    [Fact]
    public void Register_RefusesIgnorableSurfaceType()
    {
        Assert.Throws<ArgumentException>(() => ProbePayload.Register(SessionEventTypes.UserMessage, ignorable: true));
    }

    [Fact]
    public async Task ConcurrentRegistration_PreservesSessionRoundTripsAndIgnorableMarkers()
    {
        const int workerCount = 4;
        const int registrationsPerWorker = 128;
        var prefix = $"compat-probe/concurrent-{Guid.NewGuid():N}";
        var cancellation = TestContext.Current.CancellationToken;
        using var start = new Barrier(workerCount);
        var workers = Enumerable.Range(0, workerCount).Select(worker => Task.Factory.StartNew(() =>
        {
            var session = Session.Create(SessionId.Create(Guid.NewGuid().ToString("N")));
            start.SignalAndWait(cancellation);
            for (var index = 0; index < registrationsPerWorker; index++)
            {
                var core = session.Append(new TurnStartPayload(index + 1));
                var coreJson = JsonSerializer.Serialize(core, DshJson.Options);
                var restoredCore = JsonSerializer.Deserialize<SessionEvent>(coreJson, DshJson.Options)!;
                Assert.Equal(core.Data, Assert.IsType<TurnStartPayload>(restoredCore.Data));
                Assert.Equal(core.Seq, restoredCore.Seq);
                Assert.Equal(SessionEventTypes.TurnStart, restoredCore.Type);
                Assert.False(restoredCore.Ignorable);

                var type = $"{prefix}/{worker}/{index}";
                for (var version = 0; version < 2; version++)
                {
                    var ignorable = version == 0;
                    var note = $"worker={worker};event={index};registration={version}";
                    ProbePayload.Register(type, ignorable);
                    Assert.True(SessionEventCodec.IsRegistered(type));
                    Assert.Equal(ignorable, SessionEventCodec.IsIgnorable(type));
                    var annotation = session.Append(new ProbePayload(type, note));
                    Assert.Equal(ignorable, annotation.Ignorable);

                    var json = JsonSerializer.Serialize(annotation, DshJson.Options);
                    var restored = JsonSerializer.Deserialize<SessionEvent>(json, DshJson.Options)!;
                    var payload = Assert.IsType<ProbePayload>(restored.Data);
                    Assert.Equal(type, restored.Type);
                    Assert.Equal(type, payload.Type);
                    Assert.Equal(note, payload.Note);
                    Assert.Equal(annotation.Seq, restored.Seq);
                    Assert.Equal(annotation.Time, restored.Time);
                    Assert.Equal(ignorable, restored.Ignorable);
                }
            }
        }, cancellation, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        await Task.WhenAll(workers);
    }

    [Fact]
    public void ReaderWithoutTheType_SkipsIgnorableEvent()
    {
        var id = WriteLog(new ProbePayload(IgnorableType, "annotation"));

        var events = LegacyRead(id);

        Assert.Equal(
            [
                (SessionEventTypes.TurnStart, nameof(TurnStartPayload)),
                (IgnorableType, UnknownPayloadName),
                (SessionEventTypes.TurnEnd, nameof(TurnEndPayload)),
            ],
            events);
    }

    /**
     * 本 harness 不认识的"必须类型"不再被跳过, 而是按不透明记录保留 ——
     * 跳过会逼着读侧重排序号, 序号一重排, 指向别的节点的事件就全错位(见 A2)。
     */
    [Fact]
    public void ReaderWithoutTheType_PreservesRequiredEventAsOpaqueAndOpens()
    {
        var id = WriteLog(new ProbePayload(RequiredType, "state change"));

        var events = LegacyRead(id);

        Assert.Equal(
            [
                (SessionEventTypes.TurnStart, nameof(TurnStartPayload)),
                (RequiredType, nameof(UnknownSessionEventPayload)),
                (SessionEventTypes.TurnEnd, nameof(TurnEndPayload)),
            ],
            events);
    }

    [Fact]
    public void ReaderWithoutSchedulePlugin_PreservesReminderAndUnknownEvents()
    {
        using var schedules = new ScheduleService(new Dsh.Runtime.Context());
        var change = new ScheduleChangePayload(new ScheduleChange(ScheduleOperation.Create,
            new ScheduleRecord("schedule-compat", "check", "check CI", new ScheduleRule(ScheduleKind.After, DateTimeOffset.UtcNow.AddMinutes(1)))));
        var id = WriteLog(change);
        var rewritten = new List<string>();

        var events = LegacyRead(id, rewritten);

        Assert.Equal(UnknownPayloadName, events.Single(item => item.Type == ScheduleChangePayload.EventType).Payload);
        var restored = rewritten.Select(json => JsonSerializer.Deserialize<SessionEvent>(json, DshJson.Options)!).ToList();
        var reminder = restored.Single(item => item.Type == ScheduleChangePayload.EventType);
        Assert.True(reminder.Ignorable);
        Assert.Equal(change, Assert.IsType<ScheduleChangePayload>(reminder.Data));
    }

    [Fact]
    public void Header_ParentCallId_RoundTripsAndStaysNullWhenAbsent()
    {
        var parent = NewHeader();
        var child = NewHeader() with { ParentSession = parent.Id, ParentCallId = ToolCallId.Create("call-spawn-7"), Origin = "subagent" };
        using (var writer = new JsonlSessionPersistence(_root))
        {
            foreach (var header in (SessionHeader[])[parent, child])
            {
                using var handle = writer.Create(header);
                handle.Append([new SessionEvent { Type = SessionEventTypes.TurnStart, Seq = 0, Time = header.CreatedAt, Data = new TurnStartPayload(1) }]);
                handle.Close();
            }
        }

        using var reader = new JsonlSessionPersistence(_root);
        Assert.Equal(ToolCallId.Create("call-spawn-7"), reader.Stat(child.Id)?.Header.ParentCallId);
        Assert.Null(reader.Stat(parent.Id)?.Header.ParentCallId);
        Assert.Throws<JsonException>(() => (parent with { ParentCallId = ToolCallId.Create("call-orphan") }).Validate());
    }

    private SessionHeader NewHeader() => new()
    {
        Version = SessionHeader.SessionFormatVersion,
        Id = SessionId.Create(Guid.NewGuid().ToString("N")),
        CreatedAt = 1700000000000,
        Cwd = _root,
        IsSeeded = false,
    };

    private SessionId WriteLog(SessionEventPayload probe)
    {
        var header = NewHeader();
        var id = header.Id;
        var session = Session.Create(id, header: header);
        session.Append(new TurnStartPayload(1));
        session.Append(probe);
        session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        using var persistence = new JsonlSessionPersistence(_root);
        using var handle = persistence.Create(header);
        handle.Append(session.SnapshotEvents());
        handle.Close();
        return id;
    }

    /** 在隔离的加载上下文里走持久化读路径，返回每个事件的类型与解出的 payload 类名。 */
    private List<(string Type, string Payload)> LegacyRead(SessionId id, List<string>? rewritten = null)
    {
        var context = new LegacyHarnessContext();
        try
        {
            var core = context.LoadFromAssemblyName(new AssemblyName("Dsh.Core"));
            Assert.NotSame(typeof(SessionEventCodec).Assembly, core);
            var codec = core.GetType("Dsh.Core.SessionEventCodec", throwOnError: true)!;
            Assert.False((bool)codec.GetMethod(nameof(SessionEventCodec.IsRegistered))!.Invoke(null, [IgnorableType])!);
            var persistenceAssembly = context.LoadFromAssemblyName(new AssemblyName("Dsh.Persistence"));
            var persistenceType = persistenceAssembly.GetType(typeof(JsonlSessionPersistence).FullName!, throwOnError: true)!;
            var constructor = persistenceType.GetConstructors().Single();
            var arguments = constructor.GetParameters().Select((parameter, index) => index == 0 ? _root : parameter.DefaultValue).ToArray();
            var persistence = (IDisposable)constructor.Invoke(arguments);
            using (persistence)
            {
                var sessionIdType = context.LoadFromAssemblyName(new AssemblyName("Dsh.Llm")).GetType(typeof(SessionId).FullName!, throwOnError: true)!;
                var legacyId = sessionIdType.GetMethod(nameof(SessionId.Create))!.Invoke(null, [id.Value]);
                var access = Enum.Parse(core.GetType(typeof(SessionAccess).FullName!, throwOnError: true)!, nameof(SessionAccess.Read));
                var open = core.GetType(typeof(ISessionPersistence).FullName!, throwOnError: true)!.GetMethod(nameof(ISessionPersistence.Open))!;
                using var handle = (IDisposable)open.Invoke(persistence, [legacyId, access])!;
                var read = core.GetType(typeof(ISessionHandle).FullName!, throwOnError: true)!.GetMethod(nameof(ISessionHandle.Read))!;
                var events = (IEnumerable)read.Invoke(handle, [0L, null])!;
                var result = events.Cast<object>().ToList();
                if (rewritten is not null)
                {
                    var converterType = core.GetType(typeof(SessionEventJsonConverter).FullName!, throwOnError: true)!;
                    var converter = Activator.CreateInstance(converterType)!;
                    var write = converterType.GetMethod(nameof(SessionEventJsonConverter.Write))!;
                    foreach (var sessionEvent in result)
                    {
                        using var stream = new MemoryStream();
                        using (var writer = new Utf8JsonWriter(stream))
                            write.Invoke(converter, [writer, sessionEvent, DshJson.Options]);
                        rewritten.Add(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
                    }
                }
                return [.. result.Select(Describe)];
            }
        }
        finally
        {
            context.Unload();
        }
    }

    private static (string Type, string Payload) Describe(object sessionEvent)
    {
        var type = sessionEvent.GetType();
        return ((string)type.GetProperty(nameof(SessionEvent.Type))!.GetValue(sessionEvent)!,
            type.GetProperty(nameof(SessionEvent.Data))!.GetValue(sessionEvent)!.GetType().Name);
    }

    private sealed class LegacyHarnessContext() : AssemblyLoadContext("legacy-harness", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = Path.Combine(AppContext.BaseDirectory, $"{assemblyName.Name}.dll");
            return assemblyName.Name?.StartsWith("Dsh.", StringComparison.Ordinal) == true && File.Exists(path)
                ? LoadFromAssemblyPath(path)
                : null;
        }
    }

    private sealed record ProbePayload(string EventType, string Note) : SessionEventPayload
    {
        public override string Type => EventType;

        public static void Register(string type, bool ignorable)
            => SessionEventCodec.Register(
                type,
                (element, _) => new ProbePayload(type, element.GetProperty("note").GetString() ?? ""),
                static (ProbePayload payload, Utf8JsonWriter writer, JsonSerializerOptions _) =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("note", payload.Note);
                    writer.WriteEndObject();
                },
                ignorable);
    }
}
