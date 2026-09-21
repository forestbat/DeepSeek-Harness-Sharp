using Dsh.Boot;
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
        var project = CreateTempDirectory("project");
        var home = CreateTempDirectory("home");
        try
        {
            File.WriteAllText(Path.Combine(project, "a.txt"), "one");
            var ctx = new Context();
            var sessions = new SessionStore(ctx);
            var policy = new CheckpointPolicy { Enabled = true, MaxPoints = 256, KeepDays = 15 };
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
            DeleteQuietly(project);
            DeleteQuietly(home);
        }
    }

    [Fact]
    public async Task Service_RestoresFilesFromCheckpoint()
    {
        var project = CreateTempDirectory("project");
        var home = CreateTempDirectory("home");
        try
        {
            File.WriteAllText(Path.Combine(project, "keep.txt"), "keep-original");
            File.WriteAllText(Path.Combine(project, "gone.txt"), "to-be-deleted");
            var ctx = new Context();
            var sessions = new SessionStore(ctx);
            var policy = new CheckpointPolicy { Enabled = true, MaxPoints = 256, KeepDays = 15 };
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
            DeleteQuietly(project);
            DeleteQuietly(home);
        }
    }

    [Fact]
    public async Task Service_RespectsGitignoreAndPrunesOldPoints()
    {
        var project = CreateTempDirectory("project");
        var home = CreateTempDirectory("home");
        try
        {
            File.WriteAllText(Path.Combine(project, ".gitignore"), "ignored.txt\n");
            File.WriteAllText(Path.Combine(project, "a.txt"), "one");
            var ctx = new Context();
            var sessions = new SessionStore(ctx);
            var policy = new CheckpointPolicy { Enabled = true, MaxPoints = 2, KeepDays = 15 };
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
            DeleteQuietly(project);
            DeleteQuietly(home);
        }
    }

    [Fact]
    public void Log_PrunesByAgeAndLimit()
    {
        var directory = CreateTempDirectory("log");
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
            DeleteQuietly(directory);
        }
    }

    [Fact]
    public void Policy_ResolveReadsSettingsAndConfig()
    {
        var section = new CheckpointsSettings { Enabled = true, MaxPoints = 4, KeepDays = 7 };
        Assert.True(CheckpointPolicy.Resolve(section).Enabled);
        Assert.Equal(4, CheckpointPolicy.Resolve(section).MaxPoints);
        var overridden = CheckpointPolicy.Resolve(new Dictionary<string, object?>
        {
            ["enabled"] = false,
            ["max_points"] = 2L,
            ["keep_days"] = 1L,
        });
        Assert.False(overridden.Enabled);
        Assert.Equal(2, overridden.MaxPoints);
        Assert.Equal(1, overridden.KeepDays);
        Assert.False(CheckpointPolicy.Resolve(null).Enabled);
    }

    private static SessionHeader Header(string cwd) => new()
    {
        Version = SessionHeader.SessionFormatVersion,
        Id = SessionId.Create($"session-{Guid.NewGuid():N}"),
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Cwd = cwd,
        IsSeeded = false,
    };

    private static void AppendToolResult(Session session)
        => session.Append(
            new ToolResultPayload(
                1,
                1,
                MessageFactory.CreateToolResultMessage(ToolCallId.Create("call-1"), [new TextBlock("ok")], false)),
            new SurfaceOp.Append());

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    private static string CreateTempDirectory(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dsh-ckpt-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            Directory.Delete(directory, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
