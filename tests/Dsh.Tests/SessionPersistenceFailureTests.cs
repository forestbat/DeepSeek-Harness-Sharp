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

    [Fact]
    public async Task ForeignAppend_LatchesOnce_AndSurfacesOnFlush()
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

        Assert.Equal(afterSmuggle, new FileInfo(logPath).Length);
        var errors = logging.Memory.Snapshot()
            .Where(message => message.Name == "sessionPersistence" &&
                              message.Text.Contains("persistence failed", StringComparison.Ordinal))
            .ToList();
        var failure = Assert.Single(errors);
        Assert.Contains(session.Id.Value, failure.Text, StringComparison.Ordinal);

        // 结束这一轮的人必须拿到原因, 而不是以为这一轮正常收尾。
        var flush = await Assert.ThrowsAsync<AggregateException>(() =>
            ctx.Events.Parallel(new SessionFlushNotification(session)));
        Assert.Contains(flush.InnerExceptions, error => error is SessionPersistenceCorruptionException);
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

    [Fact]
    public async Task LeaseHeldElsewhere_LatchesOnce_AndSurfacesOnFlush()
    {
        using var logging = LoggingSetup.Create(new LoggingOptions { Level = "Debug" });
        var ctx = new Context(logging);
        using var persistence = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        var session = CreateSession();

        // 另一个写者先把会话写出来并**继续握着**租约(真实事故里是 TUI 还在跑)。
        using var rival = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        using var competitor = rival.Create(session.Header);
        competitor.Append([session.Append(new TurnStartPayload(1))]);
        competitor.Flush();

        using var wiring = PersistencePlugin.WirePersistence(ctx, persistence);
        // 第二个运行时的接管尝试被租约挡住, 且只报一次, 不是每条事件都撞一遍。
        AppendAndPublish(ctx, session, new StepStartPayload(1, 1));
        AppendAndPublish(ctx, session, new StepStartPayload(1, 2));

        // 日志里仍然只有第一个写者那一条事件: 第二个运行时的任何事件都没有混进去。
        using (var reader = persistence.Open(session.Id, SessionAccess.Read))
            Assert.Single(reader.Read());

        var flush = await Assert.ThrowsAsync<AggregateException>(() =>
            ctx.Events.Parallel(new SessionFlushNotification(session)));
        Assert.Contains(flush.InnerExceptions, error => error is SessionLeaseHeldException);
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
