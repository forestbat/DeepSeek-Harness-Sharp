using Dsh.GitInspect;
using Dsh.RemoteHost;
using Dsh.Transport;
using Dsh.Tui;

namespace Dsh.Tests;

/** host.git.* against a real host and a real repository with linked worktrees. */
[Collection(SerialProcessCollection.Name)]
public sealed class RemoteHostGitTests : IDisposable
{
    private readonly GitTestRepository _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _repo.Dispose();

    [Fact(Explicit = true)]
    public async Task StatusDiffAndLog_DescribeTheRepository()
    {
        var first = _repo.Commit(("a.txt", "one\n"));
        _repo.Commit(("b.txt", "two\n"));
        _repo.Write("a.txt", "one\nchanged\n");
        _repo.Write("new.txt", "fresh\n");
        await using var host = await HarnessHostFixture.CreateAsync("mgs", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("git", Ct);
        await HarnessHostFixture.OpenProjectAsync(client, _repo.Sandbox, Ct);

        var status = await client.Git.GitStatusAsync(_repo.Root, Ct);
        var diff = await client.Git.GitDiffAsync(new RemoteGitDiffRequest(_repo.Root, GitDiffTarget.WorkingTree), Ct);
        var commitDiff = await client.Git.GitDiffAsync(new RemoteGitDiffRequest(_repo.Root, GitDiffTarget.OfCommit(first)), Ct);
        var log = await client.Git.GitLogAsync(new RemoteGitLogRequest(_repo.Root, MaxCount: 1), Ct);

        Assert.True(client.Supports(GitProtocol.Capability));
        Assert.Equal("main", status.Branch.Head);
        Assert.Equal(new GitFileChange("a.txt", GitChangeKind.Modified), Assert.Single(status.Unstaged));
        Assert.Equal(["new.txt"], status.Untracked);
        var patch = Assert.Single(diff.Files);
        Assert.Contains("+changed", patch.Patch, StringComparison.Ordinal);
        Assert.Equal(GitChangeKind.Added, Assert.Single(commitDiff.Files).Kind);
        Assert.Equal("commit 2", Assert.Single(log).Subject);
    }

    [Fact(Explicit = true)]
    public async Task TuiAndRemoteDiff_UseTheSameStructuredStatistics()
    {
        _repo.Commit(("old.txt", "alpha\nbeta\ngamma\ndelta\n"), ("logo.bin", "\0original"));
        var path = OperatingSystem.IsWindows() ? "中文 [file].txt" : "中文\t[file]\n.txt";
        _repo.Git("mv", "old.txt", path);
        _repo.Write(path, "alpha\nbeta\ngamma\ndelta\nadded\n");
        _repo.Write("logo.bin", "\0changed");
        await using var host = await HarnessHostFixture.CreateAsync("mgt", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("git", Ct);
        await HarnessHostFixture.OpenProjectAsync(client, _repo.Sandbox, Ct);

        var remote = await client.Git.GitDiffAsync(new RemoteGitDiffRequest(_repo.Root, GitDiffTarget.Uncommitted), Ct);
        var summary = await new GitInspector(_repo.Root).DiffSummaryAsync(GitDiffTarget.Uncommitted, cancellationToken: Ct);
        var lines = await ChatWindow.LoadGitLinesAsync(new GitService(), _repo.Root, Ct);

        Assert.Equal(remote.Files.Select(file => new GitFileStat(file.Path, file.OriginalPath, file.Kind, file.Added, file.Deleted)), summary.Files);
        Assert.Equal(GitPanel.Format(summary, "未提交改动"), lines);
        Assert.Equal(2, remote.Files.Count);
        Assert.Contains(remote.Files, file => file.Path == path && file.OriginalPath == "old.txt" && file.Kind == GitChangeKind.Renamed && file.Added == 1 && file.Deleted == 0);
        Assert.True(Assert.Single(remote.Files, file => file.Path == "logo.bin").IsBinary);
    }

    [Fact(Explicit = true)]
    public async Task Worktrees_AreListedAddedAndRemoved()
    {
        _repo.Commit(("a.txt", "one\n"));
        await using var host = await HarnessHostFixture.CreateAsync("mgw", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("git", Ct);
        await HarnessHostFixture.OpenProjectAsync(client, _repo.Sandbox, Ct);
        var path = Path.Combine(_repo.Sandbox, "feature-wt");

        var added = await client.Git.AddWorktreeAsync(new RemoteGitWorktreeAdd(_repo.Root, path, NewBranch: "feature"), Ct);
        var listed = await client.Git.ListWorktreesAsync(_repo.Root, Ct);
        await client.Git.RemoveWorktreeAsync(new RemoteGitWorktreeRemove(_repo.Root, path), Ct);
        var after = await client.Git.ListWorktreesAsync(_repo.Root, Ct);

        Assert.Equal((path, "feature"), (added.Path, added.Branch));
        Assert.Equal([true, false], listed.Select(worktree => worktree.IsMain));
        Assert.False(Directory.Exists(path));
        Assert.Single(after);
    }

    [Fact(Explicit = true)]
    public async Task ProjectsOutsideTheAllowedRoots_AreRejected()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("uses POSIX system paths");
        await using var host = await HarnessHostFixture.CreateAsync("mgr", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("git", Ct);

        var error = await Assert.ThrowsAsync<JsonRpcException>(() => client.Git.GitStatusAsync("/etc", Ct));
        await Assert.ThrowsAsync<JsonRpcException>(() => client.Git.AddWorktreeAsync(new RemoteGitWorktreeAdd(_repo.Root, "/etc/dsh-worktree"), Ct));

        Assert.Contains("outside", error.Message, StringComparison.Ordinal);
    }
}



