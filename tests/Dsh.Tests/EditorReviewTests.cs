using Dsh.Checkpoints;
using Dsh.Core;
using Dsh.Ide.Surfaces;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class EditorReviewTests
{
    [Fact(Timeout = 30000)]
    public async Task CheckpointReview_Uses_Own_Session_StorageRoot_And_Current_Browsing_Root()
    {
        using var repo = new GitTestRepository();
        repo.Write("src/source.cs", "own baseline");
        repo.Write("outside.cs", "outside baseline");
        var ctx = new Context();
        var sessions = new SessionStore(ctx);
        using var checkpoints = new CheckpointService(ctx, CheckpointPolicy.Resolve(null), Path.Combine(repo.Sandbox, "checkpoints"));
        ctx.Provide(IEditorCheckpointReview.ServiceName, checkpoints);
        var own = CreateSession(sessions, repo.Root);
        AppendResult(own);
        await WaitForPointAsync(checkpoints, repo.Root, own.Id.Value);
        var baseline = Assert.Single(checkpoints.PointsFor(repo.Root));
        repo.Write("src/source.cs", "other session baseline");
        var other = CreateSession(sessions, repo.Root);
        AppendResult(other);
        await WaitForPointAsync(checkpoints, repo.Root, other.Id.Value);
        repo.Write("src/source.cs", "current content");
        repo.Write("outside.cs", "outside changed");
        var browsingRoot = Path.Combine(repo.Root, "src");
        own.Append(new WorkingDirectoryChangedPayload(browsingRoot));
        var reviews = new EditorReviewService(ctx, Path.Combine(repo.Sandbox, "review"));
        var review = Assert.Single(await reviews.PrepareCheckpointAsync(own, own.WorkingDirectory!, baseline.Seq, TestContext.Current.CancellationToken));
        Assert.Equal(Path.Combine(browsingRoot, "source.cs"), review.Path);
        Assert.Equal("own baseline", File.ReadAllText(review.Left));
        Assert.Equal("current content", File.ReadAllText(review.Right));
        Assert.Null(checkpoints.FindSnapshot(repo.Root, "unrelated-session", long.MaxValue));
    }

    [Fact]
    public async Task GitReview_Preserves_Literal_Unix_Backslashes_In_Object_Paths()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("A backslash is a filename character only on Unix.");
        using var repo = new GitTestRepository();
        const string path = @"literal\name.txt";
        repo.Commit((path, "before"));
        repo.Write(path, "after");
        var reviews = new EditorReviewService(new Context(), Path.Combine(repo.Sandbox, "review"));
        var review = Assert.Single(await reviews.PrepareGitAsync(repo.Root, TestContext.Current.CancellationToken));
        Assert.Equal(Path.Combine(repo.Root, path), review.Path);
        Assert.Equal("before", File.ReadAllText(review.Left));
        Assert.Equal("after", File.ReadAllText(review.Right));
    }

    [Fact]
    public async Task GitReview_Snapshots_Directory_Symlink_Text_Without_Reading_Its_Target()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("This test exercises native Unix Git symlink entries.");
        using var repo = new GitTestRepository();
        var link = Path.Combine(repo.Root, "linked");
        var first = Directory.CreateDirectory(Path.Combine(repo.Sandbox, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(repo.Sandbox, "second")).FullName;
        File.WriteAllText(Path.Combine(second, "private-content.txt"), "must not be read");
        Directory.CreateSymbolicLink(link, first);
        repo.Commit();
        Directory.Delete(link);
        Directory.CreateSymbolicLink(link, second);
        var reviews = new EditorReviewService(new Context(), Path.Combine(repo.Sandbox, "review"));
        var review = Assert.Single(await reviews.PrepareGitAsync(repo.Root, TestContext.Current.CancellationToken));
        Assert.Equal(link, review.Path);
        Assert.Equal(first, File.ReadAllText(review.Left));
        Assert.Equal(second, File.ReadAllText(review.Right));
    }

    [Fact]
    public async Task GitReview_Retains_A_File_Deletion_When_A_Directory_Replaces_It()
    {
        using var repo = new GitTestRepository();
        const string path = "node.txt";
        repo.Commit((path, "deleted content"));
        var target = Path.Combine(repo.Root, path);
        File.Delete(target);
        Directory.CreateDirectory(target);
        var reviews = new EditorReviewService(new Context(), Path.Combine(repo.Sandbox, "review"));
        var review = Assert.Single(await reviews.PrepareGitAsync(repo.Root, TestContext.Current.CancellationToken));
        Assert.Equal(target, review.Path);
        Assert.Equal("deleted content", File.ReadAllText(review.Left));
        Assert.Equal("", File.ReadAllText(review.Right));
        Assert.True(Directory.Exists(target));
    }

    private static Session CreateSession(SessionStore sessions, string cwd)
    {
        var id = SessionId.Create(Guid.NewGuid().ToString("N"));
        return sessions.Create(id, header: new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = id,
            CreatedAt = 0,
            Cwd = cwd,
            IsSeeded = false,
        });
    }

    private static void AppendResult(Session session)
        => session.Append(new ToolResultPayload(1, 1,
            MessageFactory.CreateToolResultMessage(ToolCallId.Create(Guid.NewGuid().ToString("N")), [new TextBlock("ok")], false)), new SurfaceOp.Append());

    private static async Task WaitForPointAsync(CheckpointService checkpoints, string workspace, string sessionId)
    {
        const int pollMilliseconds = 20;
        while (!checkpoints.PointsFor(workspace).Any(point => point.Session == sessionId))
            await Task.Delay(pollMilliseconds, TestContext.Current.CancellationToken);
    }
}
