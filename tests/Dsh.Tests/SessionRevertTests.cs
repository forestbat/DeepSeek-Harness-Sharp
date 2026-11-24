using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 阶段 4a: 回退是**只追加**的事件(session/revert), 而不是把日志截短。
 *
 * 要点有三: 日志变长不变短(其他读者不会看到文件缩水)、表面上被收掉的那一段不再进模型上下文、
 * 事件本身写清了它收掉的是哪些节点(别的读者折到同一条事件得到同一个结果)。
 */
public sealed class SessionRevertTests : IDisposable
{
    private const string TestCwd = "/tmp/dsh-revert-proj";

    private readonly List<string> _roots = [];

    private string NewRoot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dsh-revert-{Guid.NewGuid():N}");
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

    /** 两轮对话, 每轮一条用户消息 + 一条助手消息: 第二轮正好可以整段回退掉。 */
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
        AppendTurn(session, 1, "第一轮");
        AppendTurn(session, 2, "第二轮");
        return session;
    }

    private static void AppendTurn(Session session, int turn, string text)
    {
        session.Append(new TurnStartPayload(turn));
        session.Append(new StepStartPayload(turn, 1));
        session.Append(
            new UserMessagePayload(MessageFactory.CreateUserText($"{text}: 用户")),
            new SurfaceOp.Append());
        session.Append(
            new AssistantMessagePayload(turn, 1, MessageFactory.CreateAssistantMessage([new TextBlock($"{text}: 助手")], "p", "m")),
            new SurfaceOp.Append());
        session.Append(new TurnEndPayload(turn, new TurnEndReason.Completed()));
    }

    private static IReadOnlyList<string> Texts(Session session)
        => [.. session.DeriveMessages().Select(message => MessageText.Flatten(message.Content))];

    [Fact]
    public void Revert_AppendsAnEvent_AndHidesTheRangeFromTheSurface()
    {
        var session = BuildSession();
        var secondTurn = 5L;
        Assert.Equal(["第一轮: 用户", "第一轮: 助手", "第二轮: 用户", "第二轮: 助手"], Texts(session));
        var before = session.Seq;

        var revert = session.Revert(secondTurn, "test");
        Assert.NotNull(revert);
        // 只追加: 日志变长一条, 被回退的事件仍然在日志里(取证、计费、别的读者都要能看到)。
        Assert.Equal(before + 1, session.Seq);
        Assert.Equal(before + 1, session.SnapshotEvents().Count);
        Assert.IsType<SessionRevertPayload>(revert.Data);
        // 表面与模型上下文只剩第一轮。
        Assert.Equal(["第一轮: 用户", "第一轮: 助手"], Texts(session));
    }

    [Fact]
    public void Revert_Repeatedly_NarrowsTheSurfaceToTheEarliestTarget()
    {
        var session = BuildSession();
        session.Revert(5);   // 收掉第二轮
        session.Revert(0);   // 再收掉第一轮

        Assert.Empty(Texts(session));
        Assert.Equal(2, session.SnapshotEvents().Count(ev => ev.Data is SessionRevertPayload));
        Assert.Equal(12, session.Seq);
    }

    [Fact]
    public void Revert_WithoutSurfaceTargets_RecordsIntentOnly()
    {
        var session = BuildSession();
        var before = session.DeriveMessages().Count;
        // 回退到"日志末尾"之后: 表面上没有东西可收, 事件照样落下(只记录意图)。
        var revert = session.Revert(session.Seq);
        Assert.NotNull(revert);
        Assert.Null(revert.SurfaceOp);
        Assert.Equal(before, session.DeriveMessages().Count);
    }

    [Fact]
    public void Revert_RejectsTheInheritedSeedRange()
    {
        var session = BuildSession();
        var forkedId = SessionId.Create(Guid.NewGuid().ToString("N"));
        var forked = Session.Create(
            forkedId,
            seed: session.SnapshotEvents(),
            header: new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = forkedId,
                CreatedAt = 1700000000000,
                Cwd = TestCwd,
                IsSeeded = true,
                ParentSession = session.Header.Id,
            },
            inheritedEventCount: session.Seq);
        Assert.Null(forked.Revert(0));
        Assert.NotNull(forked.Revert(forked.InheritedEventCount));
    }

    [Fact]
    public void Revert_SurvivesAReloadFromDisk()
    {
        var root = NewRoot();
        var persistence = new JsonlSessionPersistence(root);
        var session = BuildSession();
        using (var handle = persistence.Create(session.Header))
        {
            handle.Append(session.SnapshotEvents());
            handle.Flush();
        }
        var expected = Texts(session);
        var revert = session.Revert(5)!;
        using (var handle = persistence.Open(session.Header.Id, SessionAccess.Write))
        {
            handle.Append([revert]);
            handle.Flush();
        }

        var reopened = persistence.Open(session.Header.Id, SessionAccess.Read);
        using (reopened)
        {
            var events = reopened.Read();
            // 从日志恢复时会把一段 seed 当成"历史"并在末尾补 session/end-seed, 所以条数会比写入的多一条。
            var reloaded = Session.FromRestore(session.Header.Id, events, session.Header, 0);
            Assert.Equal(expected.Take(2), Texts(reloaded));
            // 被回退的事件仍在磁盘上: 读到的日志里既有它们, 也有那条回退。
            Assert.Single(events, ev => ev.Data is SessionRevertPayload marked && marked.TargetSeq == 5);
            Assert.Contains(events, ev => ev.Seq == 5);
        }
    }

    [Fact]
    public void Revert_IsIdempotentForTheSurface()
    {
        var session = BuildSession();
        var revert = session.Revert(5)!;
        // 同一份日志折两遍必须得到同一个表面(派生缓存/窗口读都依赖这一点)。
        var first = Surface.Fold(session.SnapshotEvents());
        var second = Surface.Fold(session.SnapshotEvents());
        Assert.Equal(first.Nodes, second.Nodes);
        Assert.Contains(first.Replacements, replacement => replacement.Seq == revert.Seq);
        Assert.DoesNotContain(first.Nodes, node => node >= 5 && node < revert.Seq);
    }
}
