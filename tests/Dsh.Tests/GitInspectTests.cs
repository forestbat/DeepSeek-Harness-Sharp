using System.Diagnostics;
using Dsh.GitInspect;

namespace Dsh.Tests;

public sealed class GitInspectTests : IDisposable
{
    private readonly GitTestRepository _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task Status_SeparatesStagedUnstagedUntrackedAndRenames()
    {
        _repo.Commit(("a.txt", "a\n"), ("b.txt", "b\n"));
        _repo.Write("a.txt", "a changed\n");
        _repo.Write("c.txt", "c\n");
        _repo.Git("add", "c.txt");
        _repo.Git("mv", "b.txt", "d.txt");
        _repo.Write("with space.txt", "untracked\n");

        var status = await new GitInspector(_repo.Root).StatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal("main", status.Branch.Head);
        Assert.NotNull(status.Branch.Oid);
        Assert.Null(status.Branch.Upstream);
        Assert.Null(status.Branch.Ahead);
        Assert.Contains(new GitFileChange("a.txt", GitChangeKind.Modified), status.Unstaged);
        Assert.Contains(new GitFileChange("c.txt", GitChangeKind.Added), status.Staged);
        Assert.Contains(new GitFileChange("d.txt", GitChangeKind.Renamed, "b.txt"), status.Staged);
        Assert.Equal(["with space.txt"], status.Untracked);
        Assert.Empty(status.Conflicted);
        Assert.False(status.IsClean);
    }

    [Fact]
    public async Task Status_FreshRepository_HasNoCommitAndLogIsEmpty()
    {
        var inspector = new GitInspector(_repo.Root);

        var status = await inspector.StatusAsync(TestContext.Current.CancellationToken);
        var log = await inspector.LogAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("main", status.Branch.Head);
        Assert.Null(status.Branch.Oid);
        Assert.True(status.IsClean);
        Assert.Empty(log);
    }

    [Fact]
    public async Task Log_InvalidRepositoryFailsInsteadOfReturningEmptyHistory()
    {
        var directory = Path.Combine(_repo.Root, "broken");
        System.IO.Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, ".git"), "gitdir: missing\n", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitCommandException>(() =>
            new GitInspector(directory).LogAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Status_ReportsAheadAndBehindAgainstUpstream()
    {
        _repo.Commit(("a.txt", "1\n"));
        using var clone = GitTestRepository.CloneOf(_repo);
        clone.Commit(("local.txt", "local\n"));
        _repo.Commit(("remote.txt", "remote\n"));
        clone.Git("fetch", "--quiet");

        var status = await new GitInspector(clone.Root).StatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal("origin/main", status.Branch.Upstream);
        Assert.Equal(1, status.Branch.Ahead);
        Assert.Equal(1, status.Branch.Behind);
    }

    [Fact]
    public async Task Status_ReportsMergeConflicts()
    {
        _repo.Commit(("a.txt", "base\n"));
        _repo.Git("checkout", "-q", "-b", "side");
        _repo.Commit(("a.txt", "side\n"));
        _repo.Git("checkout", "-q", "main");
        _repo.Commit(("a.txt", "main\n"));
        _repo.GitAllowFailure("merge", "-q", "side");

        var status = await new GitInspector(_repo.Root).StatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal([new GitFileChange("a.txt", GitChangeKind.Unmerged)], status.Conflicted);
    }

    [Fact]
    public async Task Diff_WorkingTreeAndStaged_ReturnPerFilePatches()
    {
        _repo.Commit(("a.txt", "one\n"), ("b.txt", "two\n"), ("logo.bin", "\0\u0001binary"));
        _repo.Write("a.txt", "one\nadded line\n");
        _repo.Write("b.txt", "two changed\n");
        _repo.Git("add", "b.txt");
        _repo.Write("logo.bin", "\0\u0002binary changed");
        var inspector = new GitInspector(_repo.Root);

        var unstaged = await inspector.DiffAsync(GitDiffTarget.WorkingTree, cancellationToken: TestContext.Current.CancellationToken);
        var staged = await inspector.DiffAsync(GitDiffTarget.Staged, cancellationToken: TestContext.Current.CancellationToken);

        var a = Assert.Single(unstaged.Files, file => file.Path == "a.txt");
        Assert.Equal((1, 0), (a.Added, a.Deleted));
        Assert.Contains("+added line", a.Patch);
        Assert.DoesNotContain("b.txt", a.Patch);
        Assert.True(Assert.Single(unstaged.Files, file => file.Path == "logo.bin").IsBinary);
        var b = Assert.Single(staged.Files);
        Assert.Equal(("b.txt", GitChangeKind.Modified), (b.Path, b.Kind));
        Assert.Contains("-two", b.Patch);
        Assert.False(staged.Truncated);
    }

    [Fact]
    public async Task DiffSummary_MatchesFullDiffStatisticsForEveryTarget()
    {
        _repo.Commit(("a.txt", "alpha\nbeta\ngamma\ndelta\n"), ("logo.bin", "\0original"));
        _repo.Git("mv", "a.txt", "renamed [file].txt");
        _repo.Write("staged.txt", "staged\n");
        _repo.Git("add", "staged.txt");
        _repo.Write("renamed [file].txt", "alpha\nbeta\ngamma\ndelta\nadded\n");
        _repo.Write("logo.bin", "\0changed");
        var inspector = new GitInspector(_repo.Root);
        GitDiffTarget[] targets = [GitDiffTarget.WorkingTree, GitDiffTarget.Staged, GitDiffTarget.Uncommitted, GitDiffTarget.OfCommit("HEAD")];

        foreach (var target in targets)
        {
            var summary = await inspector.DiffSummaryAsync(target, cancellationToken: TestContext.Current.CancellationToken);
            var diff = await inspector.DiffAsync(target, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(diff.Files.Select(file => new GitFileStat(file.Path, file.OriginalPath, file.Kind, file.Added, file.Deleted)), summary.Files);
        }
    }

    [Fact]
    public async Task DiffSummary_DoesNotRequestPatchesOrRunTextconv()
    {
        _repo.Commit((".gitattributes", "*.txt diff=summary\n"), ("a.txt", "original\n"), ("b.txt", "original\n"));
        _repo.Git("config", "diff.summary.textconv", "dsh-git-test-textconv-must-not-run");
        _repo.Write("a.txt", "changed\n");
        _repo.Write("b.txt", "changed too\n");
        var inspector = new GitInspector(_repo.Root);
        var ct = TestContext.Current.CancellationToken;

        var summary = await inspector.DiffSummaryAsync(GitDiffTarget.Uncommitted, cancellationToken: ct);
        var lines = await Dsh.Tui.ChatWindow.LoadGitLinesAsync(new GitService(), _repo.Root, ct);

        Assert.Equal(2, summary.Files.Count);
        Assert.All(summary.Files, file => Assert.Equal((1, 1), (file.Added, file.Deleted)));
        Assert.Equal(["未提交改动", "a.txt 1+ 1-", "b.txt 1+ 1-"], lines);
        await Assert.ThrowsAsync<GitCommandException>(() => inspector.DiffAsync(GitDiffTarget.Uncommitted, cancellationToken: ct));
    }

    [Fact]
    public async Task DiffSummary_FromSubdirectory_UsesLiteralPathFilter()
    {
        _repo.Commit(("src/[app].txt", "original\n"), ("README.txt", "original\n"));
        _repo.Write("src/[app].txt", "changed\n");
        _repo.Write("README.txt", "changed\n");
        var inspector = new GitInspector(Path.Combine(_repo.Root, "src"));

        var summary = await inspector.DiffSummaryAsync(
            GitDiffTarget.WorkingTree,
            [Path.Combine(_repo.Root, "src/[app].txt")],
            TestContext.Current.CancellationToken);

        Assert.Equal("src/[app].txt", Assert.Single(summary.Files).Path);
    }

    [Fact]
    public async Task Diff_Commit_HandlesRootCommitAndRenames()
    {
        var root = _repo.Commit(("a.txt", "alpha\nbeta\ngamma\ndelta\n"));
        _repo.Git("mv", "a.txt", "renamed.txt");
        var second = _repo.Commit();
        var inspector = new GitInspector(_repo.Root);

        var first = await inspector.DiffAsync(GitDiffTarget.OfCommit(root), cancellationToken: TestContext.Current.CancellationToken);
        var rename = await inspector.DiffAsync(GitDiffTarget.OfCommit(second), cancellationToken: TestContext.Current.CancellationToken);

        var added = Assert.Single(first.Files);
        Assert.Equal(("a.txt", GitChangeKind.Added, 4), (added.Path, added.Kind, added.Added));
        var renamed = Assert.Single(rename.Files);
        Assert.Equal(("renamed.txt", "a.txt", GitChangeKind.Renamed), (renamed.Path, renamed.OriginalPath, renamed.Kind));
        Assert.Contains("rename from a.txt", renamed.Patch);
    }

    [Fact]
    public async Task Diff_CutsPatchesAtLimitsAndKeepsRemainingFilesListed()
    {
        _repo.Commit(("big.txt", ""), ("small.txt", ""));
        _repo.Write("big.txt", string.Concat(Enumerable.Repeat("0123456789\n", 1000)));
        _repo.Write("small.txt", "small\n");
        var limits = new GitDiffLimits(MaxPatchChars: 500, MaxTotalChars: 500);

        var diff = await new GitInspector(_repo.Root).DiffAsync(GitDiffTarget.WorkingTree, limits, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(diff.Truncated);
        var big = Assert.Single(diff.Files, file => file.Path == "big.txt");
        Assert.Equal((500, true, 1000), (big.Patch.Length, big.Truncated, big.Added));
        var small = Assert.Single(diff.Files, file => file.Path == "small.txt");
        Assert.Equal(("", true), (small.Patch, small.Truncated));
    }

    [Fact]
    public async Task Diff_FromSubdirectory_ResolvesRepositoryRelativePaths()
    {
        _repo.Commit(("src/app.txt", "v1\n"), ("README.txt", "readme\n"));
        _repo.Write("src/app.txt", "v2\n");
        _repo.Write("README.txt", "readme v2\n");

        var diff = await new GitInspector(Path.Combine(_repo.Root, "src")).DiffAsync(
            GitDiffTarget.WorkingTree,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["README.txt", "src/app.txt"], diff.Files.Select(file => file.Path).Order(StringComparer.Ordinal));
        Assert.All(diff.Files, file => Assert.NotEmpty(file.Patch));
    }

    [Fact]
    public async Task Log_ReturnsNewestCommitsFirst()
    {
        _repo.Commit(("a.txt", "1\n"));
        _repo.Commit(("a.txt", "2\n"));
        var newest = _repo.Commit(("a.txt", "3\n"));

        var log = await new GitInspector(_repo.Root).LogAsync(2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, log.Count);
        Assert.Equal(newest, log[0].Sha);
        Assert.StartsWith(log[0].ShortSha, log[0].Sha);
        Assert.Equal(("commit 3", GitTestRepository.AuthorName, GitTestRepository.AuthorEmail), (log[0].Subject, log[0].AuthorName, log[0].AuthorEmail));
        Assert.Equal("commit 2", log[1].Subject);
        Assert.True(log[0].AuthorDate >= log[1].AuthorDate);
    }

    [Fact]
    public async Task Worktrees_AddListAndRemove()
    {
        _repo.Commit(("a.txt", "1\n"));
        _repo.Git("branch", "existing");
        var inspector = new GitInspector(_repo.Root);
        var featurePath = Path.Combine(_repo.Sandbox, "wt-feature");
        var existingPath = Path.Combine(_repo.Sandbox, "wt-existing");

        var feature = await inspector.AddWorktreeAsync(new GitWorktreeAddRequest(featurePath, NewBranch: "feature"), TestContext.Current.CancellationToken);
        var existing = await inspector.AddWorktreeAsync(new GitWorktreeAddRequest(existingPath, StartPoint: "existing"), TestContext.Current.CancellationToken);
        var listed = await inspector.ListWorktreesAsync(TestContext.Current.CancellationToken);
        await inspector.RemoveWorktreeAsync(featurePath, cancellationToken: TestContext.Current.CancellationToken);
        var afterRemove = await inspector.ListWorktreesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(("feature", false), (feature.Branch, feature.IsMain));
        Assert.Equal("existing", existing.Branch);
        Assert.Equal(3, listed.Count);
        Assert.True(listed[0].IsMain);
        Assert.Equal(("main", _repo.Root), (listed[0].Branch, listed[0].Path));
        Assert.Equal([_repo.Root, existingPath], afterRemove.Select(worktree => worktree.Path));
        Assert.False(Directory.Exists(featurePath));
    }

    [Fact]
    public async Task Worktrees_RemoveWithLocalChangesNeedsForce()
    {
        _repo.Commit(("a.txt", "1\n"));
        var inspector = new GitInspector(_repo.Root);
        var path = Path.Combine(_repo.Sandbox, "wt-dirty");
        await inspector.AddWorktreeAsync(new GitWorktreeAddRequest(path, NewBranch: "dirty"), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "dirty\n", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitCommandException>(() => inspector.RemoveWorktreeAsync(path, cancellationToken: TestContext.Current.CancellationToken));
        await inspector.RemoveWorktreeAsync(path, force: true, TestContext.Current.CancellationToken);

        Assert.Single(await inspector.ListWorktreesAsync(TestContext.Current.CancellationToken));
    }

    /** git 2.36+ lists worktrees NUL separated with -z; older git (as on some CI images) cannot produce it, so feed the documented shape directly. */
    [Fact]
    public void WorktreeParser_ReadsNulSeparatedRecords()
    {
        const string output = "worktree /repo\0HEAD aaa\0branch refs/heads/main\0\0worktree /repo-wt\nwith newline\0HEAD bbb\0detached\0locked busy\0\0";

        var worktrees = GitWorktreeParser.Parse(output, '\0');

        Assert.Equal(2, worktrees.Count);
        Assert.Equal((Path.GetFullPath("/repo"), "main", true), (worktrees[0].Path, worktrees[0].Branch, worktrees[0].IsMain));
        Assert.Equal((Path.GetFullPath("/repo-wt\nwith newline"), true, true, false), (worktrees[1].Path, worktrees[1].IsDetached, worktrees[1].IsLocked, worktrees[1].IsMain));
    }
}

/** A throwaway git repository under artifacts020 (inside the worktree, ignored by git) with a fixed identity and branch name. */
internal sealed class GitTestRepository : IDisposable
{
    public const string AuthorName = "Dsh Test";
    public const string AuthorEmail = "test@dsh.local";

    private int _commits;

    public GitTestRepository()
    {
        Sandbox = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/test-tmp", $"git-{Guid.NewGuid():N}"));
        Root = Path.Combine(Sandbox, "repo");
        Directory.CreateDirectory(Root);
        Git("init", "-q", "-b", "main");
        Configure();
    }

    private GitTestRepository(string sandbox, string root)
    {
        Sandbox = sandbox;
        Root = root;
    }

    /** Parent directory of Root; linked worktrees and clones are created here so cleanup removes them too. */
    public string Sandbox { get; }

    public string Root { get; }

    public static GitTestRepository CloneOf(GitTestRepository origin)
    {
        var clone = new GitTestRepository(origin.Sandbox, Path.Combine(origin.Sandbox, $"clone-{Guid.NewGuid():N}"));
        origin.Git("clone", "-q", origin.Root, clone.Root);
        clone.Configure();
        return clone;
    }

    public void Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /** Writes the files, stages everything and commits; returns the new commit sha. */
    public string Commit(params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
            Write(path, content);
        Git("add", "-A");
        Git("commit", "-q", "--allow-empty", "-m", $"commit {++_commits}");
        return Git("rev-parse", "HEAD").Trim();
    }

    public string Git(params string[] arguments)
    {
        var (exitCode, output, error) = Run(arguments);
        return exitCode == 0 ? output : throw new InvalidOperationException($"git {string.Join(' ', arguments)}: {error}");
    }

    public void GitAllowFailure(params string[] arguments) => Run(arguments);

    public void Dispose()
    {
        if (Root == Path.Combine(Sandbox, "repo"))
            TempTree.Delete(Sandbox);
    }

    private void Configure()
    {
        Git("config", "user.name", AuthorName);
        Git("config", "user.email", AuthorEmail);
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
    }

    private (int ExitCode, string Output, string Error) Run(string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = TestProcessScope.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }
}

