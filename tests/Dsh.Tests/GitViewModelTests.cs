using Dsh.GitInspect;
using Dsh.Gui.ViewModels;
using Dsh.Interaction;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/** Git 视图的视图模型对真实 host 与真实仓库: 状态分组、按分组选择 diff、提交 diff、worktree 新建与移除。 */
[Collection(SerialProcessCollection.Name)]
public sealed class GitViewModelTests : IDisposable
{
    private readonly GitTestRepository _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _repo.Dispose();

    [Fact(Explicit = true)]
    public async Task StatusIsGrouped_AndEachGroupShowsItsOwnDiff()
    {
        var first = _repo.Commit(("a.txt", "one\n"), ("b.txt", "two\n"));
        _repo.Commit(("c.txt", "three\n"));
        _repo.Write("b.txt", "two\nstaged\n");
        _repo.Git("add", "b.txt");
        _repo.Write("b.txt", "two\nstaged\nunstaged\n");
        _repo.Write("a.txt", "one\nchanged\n");
        _repo.Write("new.txt", "fresh\n");
        await using var host = await HarnessHostFixture.CreateAsync("gvs", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("git-view", Ct);
        await HarnessHostFixture.OpenProjectAsync(client, _repo.Sandbox, Ct);
        var viewModel = new GitViewModel(client.Git, client.Files, _repo.Root, RemotePaths.StyleFor((await client.InfoAsync(Ct)).Platform));

        Assert.True(await viewModel.RefreshAsync(), viewModel.Status);
        var groups = viewModel.Groups.ToDictionary(group => group.Section, group => group.Items.Select(item => (item.Code, item.Path)).ToList());
        var staged = await DiffOfAsync(viewModel, GitChangeSection.Staged, "b.txt");
        var unstaged = await DiffOfAsync(viewModel, GitChangeSection.Unstaged, "b.txt");
        var untracked = await DiffOfAsync(viewModel, GitChangeSection.Untracked, "new.txt");
        viewModel.SelectedCommit = viewModel.Commits.Single(commit => commit.Sha == first);
        await FleetConnectionTests.WaitForAsync(() => viewModel.DiffCards.Count == 2 && viewModel.DiffTitle!.Contains("commit 1", StringComparison.Ordinal));

        Assert.StartsWith("main · 无上游", viewModel.BranchSummary, StringComparison.Ordinal);
        Assert.Equal([("M", "b.txt")], groups[GitChangeSection.Staged]);
        Assert.Equal([("M", "a.txt"), ("M", "b.txt")], groups[GitChangeSection.Unstaged]);
        Assert.Equal([("?", "new.txt")], groups[GitChangeSection.Untracked]);
        Assert.Empty(groups[GitChangeSection.Conflicted]);
        Assert.Equal(["commit 2", "commit 1"], viewModel.Commits.Select(commit => commit.Subject));
        Assert.Equal([(DiffLineKind.Add, "staged")], Changes(staged));
        Assert.Equal([(DiffLineKind.Add, "unstaged")], Changes(unstaged));
        Assert.Equal([(DiffLineKind.Add, "fresh")], Changes(untracked));
        Assert.Equal(["a.txt", "b.txt"], viewModel.DiffCards.Select(card => card.Title));
        Assert.Null(viewModel.SelectedChange);
    }

    [Fact(Explicit = true)]
    public async Task Worktree_IsAddedOnANewBranch_OffersANewSession_AndIsRemovedAfterConfirmation()
    {
        _repo.Commit(("a.txt", "one\n"));
        await using var host = await HarnessHostFixture.CreateAsync("gvw", TestContext.Current.CancellationToken);
        var client = await host.ConnectAsync("git-view", Ct);
        await HarnessHostFixture.OpenProjectAsync(client, _repo.Sandbox, Ct);
        var viewModel = new GitViewModel(client.Git, client.Files, _repo.Root, RemotePaths.StyleFor((await client.InfoAsync(Ct)).Platform));
        Assert.True(await viewModel.RefreshAsync(), viewModel.Status);
        var requested = new List<GitWorktree>();
        viewModel.NewSessionRequested += requested.Add;
        var expected = Path.Combine(_repo.Sandbox, "feature-wt");

        viewModel.NewWorktreePath = "../feature-wt";
        viewModel.NewWorktreeBranch = "feature";
        await viewModel.AddWorktreeCommand.ExecuteAsync(null);
        var listed = viewModel.Worktrees.Select(worktree => (worktree.Path, worktree.Branch, worktree.IsMain)).ToList();
        await viewModel.Confirmation!.AcceptCommand.ExecuteAsync(null);
        await viewModel.RemoveWorktreeCommand.ExecuteAsync(viewModel.Worktrees[0]);
        var mainRefused = (viewModel.Confirmation, viewModel.StatusIsError);
        await viewModel.RemoveWorktreeCommand.ExecuteAsync(viewModel.Worktrees[1]);
        var existedBeforeConfirming = Directory.Exists(expected);
        await viewModel.Confirmation!.AcceptCommand.ExecuteAsync(null);

        Assert.Equal([(_repo.Root, "main", true), (expected, "feature", false)], listed);
        Assert.Equal(expected, Assert.Single(requested).Path);
        Assert.Equal((null, true), mainRefused);
        Assert.True(existedBeforeConfirming);
        Assert.False(Directory.Exists(expected));
        Assert.Equal([_repo.Root], viewModel.Worktrees.Select(worktree => worktree.Path));
        Assert.False(viewModel.StatusIsError, viewModel.Status);
    }

    private static async Task<DiffCard> DiffOfAsync(GitViewModel viewModel, GitChangeSection section, string path)
    {
        var item = viewModel.Groups.Single(group => group.Section == section).Items.Single(change => change.Path == path);
        Assert.True(await viewModel.SelectChangeAsync(item), viewModel.Status);
        Assert.Same(item, viewModel.SelectedChange);
        return Assert.Single(viewModel.DiffCards);
    }

    private static List<(DiffLineKind, string)> Changes(DiffCard card)
        => [.. card.Lines.Where(line => line.Kind != DiffLineKind.Context).Select(line => (line.Kind, line.Text))];
}


