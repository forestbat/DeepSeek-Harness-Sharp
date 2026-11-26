using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 写票的约定: 想给事件发号先领票; 票只记"谁在发号、下一个号是几、什么时候到期", 不预分配号段。
 *
 * 因此两个宿主可以各自持有写句柄、轮流追加同一份日志(号不重叠), 而"活着却再也不写"的进程
 * 不会把会话锁死 —— 票到期它自己就失效, 下一个写者从日志真实条数接着发号。
 */
public sealed class SessionWriteTicketTests : IDisposable
{
    private const string TestCwd = "/tmp/dsh-ticket";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dsh-ticket-{Guid.NewGuid():N}");

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

    /** 两个宿主轮流追加同一份日志: 序号连续、无重复、无覆盖(W1)。 */
    [Fact]
    public void TwoWriters_ShareOneLog_WithoutOverlappingSequenceNumbers()
    {
        var session = NewSession();
        using var first = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        using (var writer = first.Create(session.Header))
        {
            writer.Append([session.Append(new TurnStartPayload(1))]);
            writer.Flush();
        }

        using var second = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        using var other = second.Open(session.Id, SessionAccess.Write);
        session.AttachSequencer(other.Sequencer);
        other.Append([session.Append(new StepStartPayload(1, 1)), session.Append(new StepStartPayload(1, 2))]);
        other.Flush();

        using var reader = first.Open(session.Id, SessionAccess.Read);
        var events = reader.Read();
        Assert.Equal([0L, 1L, 2L], events.Select(sessionEvent => sessionEvent.Seq));
    }

    /** 票在**活着的**别人手里时, 领号的人拿到可重试错误而不是写坏日志。 */
    [Fact]
    public void LiveTicketOfAnotherWriter_IsNotStolen()
    {
        var session = NewSession();
        using var store = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        using (var writer = store.Create(session.Header))
        {
            writer.Append([session.Append(new TurnStartPayload(1))]);
            writer.Flush();
        }

        WriteTicket(store, session.Header, expiresUtc: DateTimeOffset.MaxValue.ToUnixTimeMilliseconds(), writerId: "somebody-else", epoch: 3, nextSeq: 99);

        using var second = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        using var other = second.Open(session.Id, SessionAccess.Write);
        session.AttachSequencer(other.Sequencer);
        Assert.Throws<SessionWriteBusyException>(() => session.Append(new StepStartPayload(1, 1)));

        // 日志没被碰过: 这一次只是先别写。
        using var reader = store.Open(session.Id, SessionAccess.Read);
        Assert.Single(reader.Read());
    }

    /** 过期的票(进程已崩)被接管, 起点是日志真实条数而不是票面记的数(W2)。 */
    [Fact]
    public void ExpiredTicket_IsTakenOver_FromTheLogHoldings()
    {
        var session = NewSession();
        using var store = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        using (var writer = store.Create(session.Header))
        {
            writer.Append([session.Append(new TurnStartPayload(1))]);
            writer.Flush();
        }

        // 票面写着 nextSeq=99(它发了号却没写成): 接管时必须忽略它, 否则日志凭空出现空洞。
        WriteTicket(store, session.Header, expiresUtc: DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds(), writerId: "dead-writer", epoch: 7, nextSeq: 99);

        using var second = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        using var other = second.Open(session.Id, SessionAccess.Write);
        session.AttachSequencer(other.Sequencer);
        other.Append([session.Append(new StepStartPayload(1, 1))]);
        other.Flush();

        using var reader = store.Open(session.Id, SessionAccess.Read);
        Assert.Equal([0L, 1L], reader.Read().Select(sessionEvent => sessionEvent.Seq));
    }

    /** 空闲写者不占发号权: 落盘完成后票文件就没了, 别人立刻能领(W3)。 */
    [Fact]
    public void IdleWriter_ReleasesTheTicketOnceEverythingIsDurable()
    {
        var session = NewSession();
        using var store = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        using (var writer = store.Create(session.Header))
        {
            writer.Append([session.Append(new TurnStartPayload(1))]);
            writer.Flush();
            Assert.False(File.Exists(TicketPath(store, session.Header)), "票应在落盘后消失, 否则空闲进程会一直占着发号权");
        }
    }

    [Fact]
    public void TicketUpdate_WithAnOpenReader_PreservesSequenceNumbers()
    {
        var path = Path.Join(_root, "ticket.json");
        Assert.Equal(0, SessionWriteTicket.NextSeq(path, storedCount: 0, count: 1));

        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            Assert.Equal(1, SessionWriteTicket.NextSeq(path, storedCount: 0, count: 1));

        Assert.Equal(2, SessionWriteTicket.Inspect(path)!.NextSeq);
        SessionWriteTicket.MarkDurable(path, durableUpTo: 2);
        Assert.Null(SessionWriteTicket.Inspect(path));
    }

    /** 关掉第一个写句柄之后, 第二个写者接着写, 序号从日志末尾继续。 */
    [Fact]
    public void ClosedWriter_LetsTheNextWriterContinueTheSequence()
    {
        var session = NewSession();
        using var first = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        using (var seeded = first.Create(session.Header))
        {
            seeded.Append([session.Append(new TurnStartPayload(1))]);
            seeded.Close();
        }

        using var second = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        using var reopened = second.Open(session.Id, SessionAccess.Write);
        session.AttachSequencer(reopened.Sequencer);
        reopened.Append([session.Append(new StepStartPayload(1, 1))]);
        reopened.Flush();

        using var reader = second.Open(session.Id, SessionAccess.Read);
        Assert.Equal(2, reader.Read().Count);
    }

    private static Session NewSession()
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

    private string TicketPath(JsonlSessionPersistence store, SessionHeader header)
    {
        var path = JsonlLayout.LogPath(_root, header.Cwd, header.Id, JsonlCompression.None);
        return Path.Join(Path.GetDirectoryName(path), "ticket.json");
    }

    /** 手写票文件: 钉住磁盘格式, 也用来构造"别人的票"这种真实运行中难以复现的状态。 */
    private void WriteTicket(JsonlSessionPersistence store, SessionHeader header, long expiresUtc,
        string writerId, long epoch, long nextSeq)
    {
        var path = TicketPath(store, header);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            $$"""{"pid":2000000,"machine":"{{Environment.MachineName}}","processStartUtc":1,"epoch":{{epoch}},"writerId":"{{writerId}}","expiresUtc":{{expiresUtc}},"nextSeq":{{nextSeq}}}""");
    }
}
