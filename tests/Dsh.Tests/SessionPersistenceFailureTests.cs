using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Runtime;
using Dsh.Runtime.Logging;
using PersistencePlugin = Dsh.Persistence.Plugin;

namespace Dsh.Tests;

/**
 * 落盘终局失败(日志被别的写入方推进、会话已被别的句柄占用)的行为约定:
 * 只报一次错, 之后不再每条事件都撞同一批错误并让异常总线打一份栈;
 * 原因在本轮 flush 时抛给结束这一轮的人, 而不是静静丢事件。
 */
public sealed class SessionPersistenceFailureTests : IDisposable
{
    private const string TestCwd = "/tmp/dsh-persist-failure";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dsh-persist-failure-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /**
     * 尾巴被塞进无法归属的字节: 当场截掉并继续写, 不把这个会话判死。
     *
     * 宿主可能连续开着几天, "等下次打开再修"等于这期间一条都不落盘。
     */
    [Fact]
    public async Task ForeignAppend_IsRepairedInPlace_AndWritingContinues()
    {
        using var logging = LoggingSetup.Create(new LoggingOptions { Level = "Debug" });
        var ctx = new Context(logging);
        // 小批次: 下面三条事件第会凑满一次落盘, 于是"日志被别的写入方推进"在落盘这一刻被发现,
        // 而不是在每条事件上各撞一次。
        using var persistence = new JsonlSessionPersistence(_root, compression: JsonlCompression.None,
            writeBatch: new JsonlWriteBatch(MaxEvents: 4, Interval: TimeSpan.FromHours(1)));
        using var wiring = PersistencePlugin.WirePersistence(ctx, persistence);
        var session = CreateSession();

        AppendAndPublish(ctx, session, new TurnStartPayload(1));
        AppendAndPublish(ctx, session, new StepStartPayload(1, 1));
        var logPath = JsonlLayout.LogPath(_root, session.Header.Cwd, session.Id, JsonlCompression.None);
        Assert.True(File.Exists(logPath));

        // 冒充"另一个进程往同一份日志追加了 85 字节"——这正是 2026-11-16 事故的触发条件。
        var smuggled = new byte[85];
        await using (var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write))
            stream.Write(smuggled);
        var afterSmuggle = new FileInfo(logPath).Length;

        // 撞上终局错误: 不再从 Emit 里抛出去(旧行为会被异常总线逐条打成栈), 也不写坏日志。
        AppendAndPublish(ctx, session, new StepStartPayload(1, 2));
        AppendAndPublish(ctx, session, new StepStartPayload(1, 3));
        AppendAndPublish(ctx, session, new StepStartPayload(1, 4));

        // 外来字节被截掉, 四条事件都写进去了。
        Assert.True(new FileInfo(logPath).Length > afterSmuggle);
        Assert.DoesNotContain(logging.Memory.Snapshot(),
            message => message.Text.Contains("persistence failed", StringComparison.Ordinal));
        using (var reader = persistence.Open(session.Id, SessionAccess.Read))
            Assert.Equal([0L, 1L, 2L, 3L, 4L], reader.Read().Select(sessionEvent => sessionEvent.Seq));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(logPath)!, $"{Path.GetFileName(logPath)}.damaged-*"));

        // 结束这一轮的人不该再看到异常: 这一轮正常收尾。
        await ctx.Events.Parallel(new SessionFlushNotification(session));
    }

    [Fact]
    public async Task HealthySession_KeepsPersistingAndFlushSucceeds()
    {
        var ctx = new Context(LoggingSetup.Create(new LoggingOptions { Level = "Debug" }));
        using var persistence = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        using var wiring = PersistencePlugin.WirePersistence(ctx, persistence);
        var session = CreateSession();

        AppendAndPublish(ctx, session, new TurnStartPayload(1));
        AppendAndPublish(ctx, session, new StepStartPayload(1, 1));
        await ctx.Events.Parallel(new SessionFlushNotification(session));

        var reader = persistence.Open(session.Id, SessionAccess.Read);
        Assert.Equal(session.OwnEvents().Count, reader.Read().Count);
        reader.Close();
    }

    /**
     * 票在**活着的**别人手里时, 领号的人拿到可重试错误: 这一次只是先别写, 日志一个字节都不动。
     *
     * 旧行为是"写权不在自己手里就停写并告诉用户", 于是另一个宿主活着就等于这份会话永久不可写。
     */
    [Fact]
    public async Task TicketHeldByAnotherWriter_RejectsTheIssueAndLeavesTheLogAlone()
    {
        using var logging = LoggingSetup.Create(new LoggingOptions { Level = "Debug" });
        var ctx = new Context(logging);
        using var persistence = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        using var wiring = PersistencePlugin.WirePersistence(ctx, persistence);
        var session = CreateSession();

        // 先把会话写出一条事件, 接线顺带把发号票接上。
        AppendAndPublish(ctx, session, new TurnStartPayload(1));
        await ctx.Events.Parallel(new SessionFlushNotification(session));

        // Keep the foreign ticket valid throughout the production retry loop, including slow CI runs.
        WriteForeignTicket(persistence, session, expiresUtc: DateTimeOffset.MaxValue.ToUnixTimeMilliseconds(), nextSeq: 99);

        Assert.Throws<SessionWriteBusyException>(() => session.Append(new StepStartPayload(1, 1)));
        using var reader = persistence.Open(session.Id, SessionAccess.Read);
        Assert.Single(reader.Read());
    }

    /**
     * 票过期(写它的进程已经崩了)之后, 下一个写者从日志真实条数接着发号并写下去。
     *
     * 这正是 2026-11-22 那条会话丢 15 小时事件的反面: 当时"写权不在自己手里"被当成终局,
     * 于是那个进程活着、续着租约、却再也不写, 别的宿主一律被挡在门外。
     */
    [Fact]
    public async Task ExpiredTicket_IsTakenOver_AndBufferedEventsLand()
    {
        using var logging = LoggingSetup.Create(new LoggingOptions { Level = "Debug" });
        var ctx = new Context(logging);
        using var persistence = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        using var wiring = PersistencePlugin.WirePersistence(ctx, persistence);
        var session = CreateSession();

        AppendAndPublish(ctx, session, new TurnStartPayload(1));
        await ctx.Events.Parallel(new SessionFlushNotification(session));

        // 票面写着 nextSeq=99, 但它已经过期: 接管时以日志真实条数(1)为起点, 不制造空洞。
        WriteForeignTicket(persistence, session, expiresUtc: DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds(), nextSeq: 99);
        AppendAndPublish(ctx, session, new StepStartPayload(1, 1));
        await ctx.Events.Parallel(new SessionFlushNotification(session));

        using var reader = persistence.Open(session.Id, SessionAccess.Read);
        Assert.Equal([0L, 1L], reader.Read().Select(sessionEvent => sessionEvent.Seq));
    }

    /** 手写票文件: 造出"别的写者持票"这种真实运行中难以复现的状态。 */
    private void WriteForeignTicket(JsonlSessionPersistence persistence, Session session, long expiresUtc, long nextSeq)
    {
        var logPath = JsonlLayout.LogPath(_root, session.Header.Cwd, session.Id, JsonlCompression.None);
        var path = Path.Join(Path.GetDirectoryName(logPath), "ticket.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            $"{{\"pid\":2000000,\"machine\":\"{Environment.MachineName}\",\"processStartUtc\":1,\"epoch\":7,\"writerId\":\"somebody-else\",\"expiresUtc\":{expiresUtc},\"nextSeq\":{nextSeq}}}");
    }

    /**
     * 落盘落后于内存(上一批没写成功、日志被回退过)时不能报废: 按磁盘真实条数把会话里缺的那一段
     * 整段补写回去, 用户的每一条事件都还在内存里。
     *
     * 2026-11-22 那份会话就是反面: 一次 `append seq mismatch`(expected 288775 got 288776) 之后
     * 整个会话永久停写, 之后的 15 小时内容全部没进日志。
     */
    [Fact]
    public async Task LogBehindMemory_RealignsAndWritesInsteadOfLatching()
    {
        using var logging = LoggingSetup.Create(new LoggingOptions { Level = "Debug" });
        var ctx = new Context(logging);
        using var persistence = new JsonlSessionPersistence(_root, compression: JsonlCompression.None,
            writeBatch: new JsonlWriteBatch(MaxEvents: 4, Interval: TimeSpan.FromHours(1)));
        using var wiring = PersistencePlugin.WirePersistence(ctx, persistence);
        var session = CreateSession();

        AppendAndPublish(ctx, session, new TurnStartPayload(1));
        await ctx.Events.Parallel(new SessionFlushNotification(session));

        // 日志被回退到只剩头行(等价于"上一批其实没写进去"): 盘上 0 条, 内存里 1 条。
        var logPath = JsonlLayout.LogPath(_root, session.Header.Cwd, session.Id, JsonlCompression.None);
        var bytes = File.ReadAllBytes(logPath);
        using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Write))
            stream.SetLength(Array.IndexOf(bytes, (byte)'\n') + 1);
        Assert.Equal(0, persistence.StoredEventCount(session.Id));

        AppendAndPublish(ctx, session, new StepStartPayload(1, 1));
        await ctx.Events.Parallel(new SessionFlushNotification(session));

        // 没有报废, 而且缺的那一条也补回去了。
        Assert.DoesNotContain(logging.Memory.Snapshot(),
            message => message.Text.Contains("persistence failed", StringComparison.Ordinal));
        using var reader = persistence.Open(session.Id, SessionAccess.Read);
        var stored = reader.Read();
        Assert.Equal(session.OwnEvents().Count, stored.Count);
        Assert.Equal([0L, 1L], stored.Select(sessionEvent => sessionEvent.Seq).ToArray());
    }

    /**
     * 压缩日志走的是帧索引这条路: 条数取索引、末尾干净与否按"索引到不到文件末尾"判。
     * 这两个判据决定能不能就地补写 —— 索引说末尾是完整的一批帧才补, 说不上就不碰。
     */
    [Fact]
    public async Task ZstdLog_ReportsStoredCountAndWhetherTheTailIsClean()
    {
        var ctx = new Context(LoggingSetup.Create(new LoggingOptions { Level = "Debug" }));
        using var persistence = new JsonlSessionPersistence(_root, compression: JsonlCompression.Zstd);
        using var wiring = PersistencePlugin.WirePersistence(ctx, persistence);
        var session = CreateSession();

        AppendAndPublish(ctx, session, new TurnStartPayload(1));
        AppendAndPublish(ctx, session, new StepStartPayload(1, 1));
        await ctx.Events.Parallel(new SessionFlushNotification(session));

        Assert.Equal(2, persistence.StoredEventCount(session.Id));
        Assert.True(persistence.IsLogTailClean(session.Id));

        // 末尾多出一段不属于任何帧的字节: 索引不再覆盖到文件末尾, 这时不许往上追加。
        var logPath = JsonlLayout.LogPath(_root, session.Header.Cwd, session.Id, JsonlCompression.Zstd);
        using (var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write))
            stream.Write(new byte[16]);
        Assert.False(persistence.IsLogTailClean(session.Id));
    }

    private static Session CreateSession()
    {
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        return Session.Create(id,
            header: new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = id,
                CreatedAt = 1700000000000,
                Cwd = TestCwd,
                IsSeeded = false
            });
    }

    /** 追加一条事件并像 SessionStore 那样把它公告出去(持久化接线就是靠这条通知落盘的)。 */
    private static void AppendAndPublish(Context ctx, Session session, SessionEventPayload payload)
        => ctx.Events.Emit(new SessionEventNotification(session, session.Append(payload)));
}
