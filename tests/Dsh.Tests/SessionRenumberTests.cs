using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 发号后被接管: 不是"忙"也不是损坏, 而是把未落盘的那一段按日志真实条数重编号。
 *
 * 场景: 本进程发出 2、3 两号并把事件交给句柄(还没落盘), 另一个宿主这时把 2 号写进了日志。
 * 正确结局是: 我们的两条整体平移到 3、4 照常写入, 日志连续无重复, 界面收到一次重编号通知。
 */
public sealed class SessionRenumberTests : IDisposable
{
    private const string TestCwd = "/tmp/dsh-renumber";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dsh-renumber-{Guid.NewGuid():N}");

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
    public void NumbersTakenOver_RenumberTheBufferedTailAndWriteIt()
    {
        using var mine = new JsonlSessionPersistence(_root, compression: JsonlCompression.None);
        var header = NewHeader();
        using var handle = mine.Create(header);
        var session = Session.Create(header.Id, header: header);
        handle.TailSource = from => [.. session.SnapshotEvents(from)];
        handle.RebaseSource = session.RebaseUnsaved;
        session.AttachSequencer(handle.Sequencer);

        // 先落两条, 让日志末尾是 2。
        handle.Append([session.Append(new TurnStartPayload(1)), session.Append(new StepStartPayload(1, 1))]);
        handle.Flush();

        // 本进程接着发出 2、3 两号并交给句柄(句柄缓冲, 还没落盘)。
        var buffered = new[] { session.Append(new StepStartPayload(1, 2)), session.Append(new TurnEndPayload(1, new TurnEndReason.Completed())) };
        handle.Append(buffered);
        Assert.Equal(4, session.Seq);

        // 另一个宿主把 2 号写进了日志(它自己按日志条数发号, 顺序完全合法)。
        using (var rival = new JsonlSessionPersistence(_root, compression: JsonlCompression.None))
        using (var rivalHandle = rival.Open(header.Id, SessionAccess.Write))
        {
            rivalHandle.Append([new SessionEvent
            {
                Type = new StepStartPayload(1, 3).Type,
                Seq = 2,
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Data = new StepStartPayload(1, 3),
            }]);
            rivalHandle.Flush();
        }

        var renumbered = new List<(long From, long Delta)>();
        session.Renumbered += (_, from, delta) => renumbered.Add((from, delta));

        // 这一批的号已经过期: 句柄让会话重编号, 然后照常写进去 —— 不抛异常、不丢事件。
        handle.Flush();

        Assert.Equal([(2L, 1L)], renumbered);
        Assert.Equal(5, session.Seq);
        using var reader = mine.Open(header.Id, SessionAccess.Read);
        var events = reader.Read();
        Assert.Equal([0L, 1L, 2L, 3L, 4L], events.Select(sessionEvent => sessionEvent.Seq));
    }

    private static SessionHeader NewHeader()
    {
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        return new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = id,
            CreatedAt = 1700000000000,
            Cwd = TestCwd,
            IsSeeded = false
        };
    }
}
