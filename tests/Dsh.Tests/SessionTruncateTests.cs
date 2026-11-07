using Dsh.Compaction;
using Dsh.Core;
using Dsh.Goal;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Runtime;
using Dsh.SessionQuery;

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

    private static (Session Session, SessionProjectionRegistry Projections) ProjectedSession()
    {
        var ctx = new Context();
        var projections = new SessionProjectionRegistry(ctx);
        projections.Register(TurnBoundaryProjectionDefinition.Instance);
        projections.Register(GoalProjectionDefinition.Instance);
        var session = new SessionStore(ctx).Create(SessionId.Create(Guid.NewGuid().ToString("N")));
        session.Append(new TurnStartPayload(1));
        session.Append(CreateGoal("goal-before-revert"));
        session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        Assert.Null(projections.StateOf<TurnBoundaryProjection>(session, TurnBoundaryProjectionDefinition.Key)!.OpenTurnStartSeq);
        Assert.NotNull(projections.StateOf<GoalProjectionState>(session, GoalProjectionDefinition.Key)!.Current);
        return (session, projections);
    }

    private static GoalChangePayload CreateGoal(string id)
        => GoalChangePayload.FromChange(new GoalChange.Snapshot(
            GoalOperation.Create,
            new GoalSnapshot(GoalId.Create(id), 1, "ship it", GoalPhase.Active, 8),
            0,
            1700000000000,
            1700000000000));

    [Fact]
    public void Projections_RebuildFromTruncatedLog()
    {
        var (session, projections) = ProjectedSession();

        Assert.True(session.TryTruncateTo(1));

        Assert.Equal(0, projections.StateOf<TurnBoundaryProjection>(session, TurnBoundaryProjectionDefinition.Key)!.OpenTurnStartSeq);
        Assert.Null(projections.StateOf<GoalProjectionState>(session, GoalProjectionDefinition.Key)!.Current);
    }

    [Fact]
    public void Projections_ApplyEventsAppendedAfterTruncation()
    {
        var (session, projections) = ProjectedSession();

        Assert.True(session.TryTruncateTo(0));
        session.Append(new TurnStartPayload(2));
        session.Append(CreateGoal("goal-after-revert"));

        var boundary = projections.StateOf<TurnBoundaryProjection>(session, TurnBoundaryProjectionDefinition.Key)!;
        Assert.Equal(0, boundary.OpenTurnStartSeq);
        Assert.Equal(2, boundary.LastTurn);
        var goal = projections.StateOf<GoalProjectionState>(session, GoalProjectionDefinition.Key)!.Current;
        Assert.Equal("goal-after-revert", goal?.Goal.Id.Value);
    }

    [Fact]
    public void TokenMeter_RemeasuresTruncatedLog()
    {
        var ctx = new Context();
        using var meter = new TokenMeter(ctx);
        var session = new SessionStore(ctx).Create(SessionId.Create(Guid.NewGuid().ToString("N")));
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("first")), new SurfaceOp.Append());
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("second")), new SurfaceOp.Append());
        Assert.Equal(2, meter.Measure(session).Nodes.Count);

        Assert.True(session.TryTruncateTo(1));
        session.Append(
            new UserMessagePayload(MessageFactory.CreateUserText(string.Join(' ', Enumerable.Repeat("replacement", 64)))),
            new SurfaceOp.Append());

        using var fresh = new TokenMeter(new Context());
        Assert.Equal(fresh.Measure(session).Nodes, meter.Measure(session).Nodes);
    }

    [Fact]
    public void SessionQuery_ReindexesTruncatedLiveSession()
    {
        var ctx = new Context();
        var session = new SessionStore(ctx).Create(SessionId.Create(Guid.NewGuid().ToString("N")));
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("keyword walrus")), new SurfaceOp.Append());
        using var service = new SessionQueryService(ctx);
        Assert.Single(service.Search("walrus"));

        Assert.True(session.TryTruncateTo(0));
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("keyword narwhal")), new SurfaceOp.Append());

        Assert.Empty(service.Search("walrus"));
        Assert.Single(service.Search("narwhal"));
    }

    [Fact]
    public void RuntimeContext_IsResentAfterTruncationDropsIt()
    {
        var ctx = new Context();
        var session = new SessionStore(ctx).Create(SessionId.Create(Guid.NewGuid().ToString("N")));
        using var projection = new RuntimeContextProjection(ctx, session);
        var snapshot = projection.Project("cwd: /work", []);
        Assert.NotNull(snapshot);
        session.Append(new UserMessagePayload(snapshot), new SurfaceOp.Append());
        Assert.Null(projection.Project("cwd: /work", []));

        Assert.True(session.TryTruncateTo(0));

        Assert.NotNull(projection.Project("cwd: /work", []));
    }
}
