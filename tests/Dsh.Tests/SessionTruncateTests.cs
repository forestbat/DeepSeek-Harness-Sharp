using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/** §6 D3: 会话就地截断(Session.TryTruncateTo)与持久化日志重写(ISessionHandle.Truncate)。 */
public sealed class SessionTruncateTests : IDisposable
{
    private const string TestCwd = "/tmp/dsh-trunc-proj";

    private readonly List<string> _roots = [];

    private string NewRoot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dsh-trunc-{Guid.NewGuid():N}");
        _roots.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static Session BuildSession()
    {
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        var session = Session.Create(id, header: new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = id,
            CreatedAt = 1700000000000,
            Cwd = TestCwd,
            IsSeeded = false,
        });
        session.Append(new TurnStartPayload(1));
        session.Append(new StepStartPayload(1, 1));
        for (var index = 0; index < 5; index += 1)
            session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, $"tok{index}")));
        session.Append(
            new AssistantMessagePayload(1, 1, MessageFactory.CreateAssistantMessage([new TextBlock("done")], "p", "m")),
            new SurfaceOp.Append(),
            [2, 3, 4, 6]);
        session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        return session;
    }

    [Fact]
    public void Session_TruncateTo_DropsTailAndAllowsReAppend()
    {
        var session = BuildSession();
        Assert.Equal(9, session.Seq);

        Assert.True(session.TryTruncateTo(5));
        Assert.Equal(5, session.Seq);
        Assert.Equal(5, session.SnapshotEvents().Count);

        var appended = session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        Assert.Equal(5, appended.Seq);
        Assert.Equal(6, session.Seq);
    }

    [Fact]
    public void Session_TruncateTo_RejectsOutOfRange()
    {
        var session = BuildSession();
        Assert.False(session.TryTruncateTo(99));
        Assert.False(session.TryTruncateTo(-1));
        Assert.Equal(9, session.Seq);
    }

    [Fact]
    public void Persistence_Handle_Truncate_RewritesLogAndContinues()
    {
        var root = NewRoot();
        using var persistence = new JsonlSessionPersistence(root, compression: JsonlCompression.None);
        var session = BuildSession();
        var events = session.SnapshotEvents();
        using var handle = persistence.Create(session.Header);
        handle.Append(events);

        handle.Truncate(5);
        Assert.Equal(5, handle.Read().Count);

        var next = new SessionEvent
        {
            Type = SessionEventTypes.TurnEnd,
            Seq = 5,
            Time = 1700000002000,
            Data = new TurnEndPayload(1, new TurnEndReason.Completed()),
        };
        handle.Append([next]);
        Assert.Equal(6, handle.Read().Count);
        handle.Close();

        var reader = persistence.Open(session.Header.Id, SessionAccess.Read);
        var reopened = reader.Read();
        reader.Close();
        Assert.Equal(6, reopened.Count);
        Assert.Equal(5, reopened[5].Seq);
    }
}
