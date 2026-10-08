using Dsh.Checkpoints;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class CheckpointTests
{
    [Fact]
    public async Task Service_RecordsPointOnToolResultAndSkipsUnchangedContent()
    {
        var project = TempTree.CreateDirectory("project");
        var home = TempTree.CreateDirectory("home");
        try
        {
            File.WriteAllText(Path.Combine(project, "a.txt"), "one");
            var ctx = new Context();
            var sessions = new SessionStore(ctx);
            var policy = new CheckpointPolicy { MaxPoints = 256, KeepDays = 15 };
            using var service = new CheckpointService(ctx, policy, home);
            var header = Header(project);
            var session = sessions.Create(id: header.Id, header: header);

            File.WriteAllText(Path.Combine(project, "a.txt"), "two");
            AppendToolResult(session);
            await WaitUntilAsync(() => service.PointsFor(project).Count == 1, "first checkpoint");

            AppendToolResult(session);
            await Task.Delay(500, TestContext.Current.CancellationToken);
            Assert.Single(service.PointsFor(project));

            File.WriteAllText(Path.Combine(project, "a.txt"), "three");
            AppendToolResult(session);
            await WaitUntilAsync(() => service.PointsFor(project).Count == 2, "second checkpoint");

            var points = service.PointsFor(project);
            Assert.NotEqual(points[0].Commit, points[1].Commit);
            Assert.True(points[1].Seq > points[0].Seq);
        }
        finally
        {
            TempTree.Delete(project);
            TempTree.Delete(home);
        }
    }

    [Fact]
    public async Task Service_RestoresFilesFromCheckpoint()
    {
        var project = TempTree.CreateDirectory("project");
        var home = TempTree.CreateDirectory("home");
        try
        {
            File.WriteAllText(Path.Combine(project, "keep.txt"), "keep-original");
            File.WriteAllText(Path.Combine(project, "gone.txt"), "to-be-deleted");
            var ctx = new Context();
            var sessions = new SessionStore(ctx);
            var policy = new CheckpointPolicy { MaxPoints = 256, KeepDays = 15 };
            using var service = new CheckpointService(ctx, policy, home);
            var header = Header(project);
            var session = sessions.Create(id: header.Id, header: header);

            AppendToolResult(session);
            await WaitUntilAsync(() => service.PointsFor(project).Count == 1, "first checkpoint");
            File.WriteAllText(Path.Combine(project, "keep.txt"), "keep-modified");
            File.Delete(Path.Combine(project, "gone.txt"));
            File.WriteAllText(Path.Combine(project, "added.txt"), "added-later");
            AppendToolResult(session);
            await WaitUntilAsync(() => service.PointsFor(project).Count == 2, "second checkpoint");

            await service.RestoreFilesAsync(project, 0, CancellationToken.None);

            Assert.Equal("keep-original", File.ReadAllText(Path.Combine(project, "keep.txt")));
            Assert.Equal("to-be-deleted", File.ReadAllText(Path.Combine(project, "gone.txt")));
            Assert.False(File.Exists(Path.Combine(project, "added.txt")));
        }
        finally
        {
            TempTree.Delete(project);
            TempTree.Delete(home);
        }
    }

    [Fact]
    public async Task Service_RespectsGitignoreAndPrunesOldPoints()
    {
        var project = TempTree.CreateDirectory("project");
        var home = TempTree.CreateDirectory("home");
        try
        {
            File.WriteAllText(Path.Combine(project, ".gitignore"), "ignored.txt\n");
            File.WriteAllText(Path.Combine(project, "a.txt"), "one");
            var ctx = new Context();
            var sessions = new SessionStore(ctx);
            var policy = new CheckpointPolicy { MaxPoints = 2, KeepDays = 15 };
            using var service = new CheckpointService(ctx, policy, home);
            var header = Header(project);
            var session = sessions.Create(id: header.Id, header: header);

            File.WriteAllText(Path.Combine(project, "a.txt"), "two");
            File.WriteAllText(Path.Combine(project, "ignored.txt"), "ignored-1");
            AppendToolResult(session);
            await WaitUntilAsync(() => service.PointsFor(project).Count == 1, "first checkpoint");

            File.WriteAllText(Path.Combine(project, "ignored.txt"), "ignored-2");
            AppendToolResult(session);
            await Task.Delay(400, TestContext.Current.CancellationToken);
            Assert.Single(service.PointsFor(project));

            File.WriteAllText(Path.Combine(project, "a.txt"), "three");
            AppendToolResult(session);
            await WaitUntilAsync(() => service.PointsFor(project).Count == 2, "second checkpoint");
            File.WriteAllText(Path.Combine(project, "a.txt"), "four");
            AppendToolResult(session);
            await WaitUntilAsync(() => service.PointsFor(project).Count == 2, "pruned to max");
            var points = service.PointsFor(project);
            Assert.Equal(2, points.Count);
            await WaitUntilAsync(() => !service.PointsFor(project).Any(point => point.Commit == points[0].Commit), "oldest point pruned");
        }
        finally
        {
            TempTree.Delete(project);
            TempTree.Delete(home);
        }
    }

    [Fact]
    public void Log_PrunesByAgeAndLimit()
    {
        var directory = TempTree.CreateDirectory("log");
        try
        {
            var path = Path.Combine(directory, "points.jsonl");
            var log = new CheckpointLog(path);
            var now = DateTimeOffset.UtcNow;
            for (var index = 0; index < 5; index++)
            {
                log.Append(new CheckpointPoint
                {
                    Timestamp = now.AddDays(-10 + index).ToUnixTimeMilliseconds(),
                    Commit = $"{index:D40}",
                    Seq = index,
                    Session = "session-x",
                    Reason = "tool/result",
                });
            }
            var removed = log.Prune(maxPoints: 2, keepDays: 15, now);
            Assert.Equal(3, removed.Count);
            Assert.Equal(2, log.Points.Count);
            Assert.Equal(3, log.Points[0].Seq);
            Assert.Equal(4, log.Points[1].Seq);
            Assert.True(File.Exists(path));
        }
        finally
        {
            TempTree.Delete(directory);
        }
    }

    [Fact]
    public void Policy_ResolveReadsParameters()
    {
        var policy = CheckpointPolicy.Resolve(new Dictionary<string, object?>
        {
            ["max_points"] = 2L,
            ["keep_days"] = 1L,
        });
        Assert.Equal(2, policy.MaxPoints);
        Assert.Equal(1, policy.KeepDays);
        var defaults = CheckpointPolicy.Resolve(null);
        Assert.Equal(CheckpointPolicy.DefaultMaxPoints, defaults.MaxPoints);
        Assert.Equal(CheckpointPolicy.DefaultKeepDays, defaults.KeepDays);
    }

    private static SessionHeader Header(string cwd) => new()
    {
        Version = SessionHeader.SessionFormatVersion,
        Id = SessionId.Create($"session-{Guid.NewGuid():N}"),
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Cwd = cwd,
        IsSeeded = false,
    };

    /** D3 revert 文件侧: RestoreToSeqAsync 取 Seq<=目标 的最近检查点; 无则 null(不动文件)。 */
    [Fact]
    public async Task RestoreToSeq_PicksLastPointAtOrBeforeSeq()
    {
        var project = TempTree.CreateDirectory("project");
        var home = TempTree.CreateDirectory("home");
        try
        {
            File.WriteAllText(Path.Combine(project, "keep.txt"), "op1");
            var ctx = new Context();
            var sessions = new SessionStore(ctx);
            using var service = new CheckpointService(ctx, new CheckpointPolicy { MaxPoints = 256, KeepDays = 15 }, home);
            var header = Header(project);
            var session = sessions.Create(id: header.Id, header: header);

            AppendToolResult(session);
            await WaitUntilAsync(() => service.PointsFor(project).Count == 1, "first checkpoint");
            var firstSeq = service.PointsFor(project)[0].Seq;

            File.WriteAllText(Path.Combine(project, "keep.txt"), "op2");
            AppendToolResult(session);
            await WaitUntilAsync(() => service.PointsFor(project).Count == 2, "second checkpoint");

            Assert.Null(await service.RestoreToSeqAsync(project, firstSeq - 1, CancellationToken.None));
            Assert.NotNull(await service.RestoreToSeqAsync(project, firstSeq, CancellationToken.None));
            Assert.Equal("op1", File.ReadAllText(Path.Combine(project, "keep.txt")));
        }
        finally
        {
            TempTree.Delete(project);
            TempTree.Delete(home);
        }
    }

    private static void AppendToolResult(Session session)
        => session.Append(
            new ToolResultPayload(
                1,
                1,
                MessageFactory.CreateToolResultMessage(ToolCallId.Create("call-1"), [new TextBlock("ok")], false)),
            new SurfaceOp.Append());

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        // CI runner 上 git 进程创建 + 磁盘/杀软扫描很慢, 首个检查点可能远超 15s; 放宽上限(满足即返回)。
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"timed out waiting for {what}");
    }
}
