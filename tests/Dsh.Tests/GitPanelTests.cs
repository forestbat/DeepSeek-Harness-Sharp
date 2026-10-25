using Dsh.Compaction;
using Dsh.GitInspect;
using Dsh.Tui;

namespace Dsh.Tests;

public sealed class CompactionModelSettingTests
{
    [Fact]
    public void Parse_Splits_Provider_And_Model()
    {
        var parsed = CompactionModelSetting.Parse("openai/gpt-4o");

        Assert.True(parsed is not null);
        Assert.Equal("openai", parsed.Value.Provider);
        Assert.Equal("gpt-4o", parsed.Value.Model);
    }

    [Fact]
    public void Parse_ModelOnly_Or_Empty_Returns_Null()
    {
        Assert.Null(CompactionModelSetting.Parse("gpt-4o"));
        Assert.Null(CompactionModelSetting.Parse("openai/"));
        Assert.Null(CompactionModelSetting.Parse("  "));
        Assert.Null(CompactionModelSetting.Parse(null));
    }

    [Fact]
    public void ModelName_Strips_Optional_Provider()
    {
        Assert.Equal("gpt-4o", CompactionModelSetting.ModelName("openai/gpt-4o"));
        Assert.Equal("gpt-4o", CompactionModelSetting.ModelName("gpt-4o"));
    }
}

public sealed class GitPanelTests : IDisposable
{
    private readonly GitTestRepository _repo = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task Refresh_UsesSharedSummaryForStagedAndUnstagedChanges()
    {
        _repo.Commit(("src/b.cs", "original\n"), ("src/a.cs", ""), ("assets/logo.png", "\0original"));
        _repo.Write("src/b.cs", "first\nsecond\nthird\n");
        _repo.Git("add", "src/b.cs");
        _repo.Write("src/a.cs", "first\nsecond\n");
        _repo.Write("assets/logo.png", "\0changed");
        var summary = await new GitInspector(_repo.Root).DiffSummaryAsync(GitDiffTarget.Uncommitted, cancellationToken: Ct);

        var lines = await ChatWindow.LoadGitLinesAsync(new GitService(), _repo.Root, Ct);

        Assert.Equal(GitPanel.Format(summary, "未提交改动"), lines);
        Assert.Equal(["未提交改动", "assets/logo.png (binary)", "src/a.cs 2+ 0-", "src/b.cs 3+ 1-"], lines);
    }

    [Fact]
    public async Task Refresh_RendersRenameAndSpecialFileNamesWithoutParsingThemAgain()
    {
        var path = OperatingSystem.IsWindows() ? "中文 [draft] file.txt" : "中文\t[draft]\nfile.txt";
        var displayedPath = OperatingSystem.IsWindows() ? "中文 [draft] file.txt" : "中文\\t[draft]\\nfile.txt";
        _repo.Commit(("old.txt", "alpha\nbeta\ngamma\ndelta\n"));
        _repo.Git("mv", "old.txt", path);
        var summary = await new GitInspector(_repo.Root).DiffSummaryAsync(GitDiffTarget.Uncommitted, cancellationToken: Ct);

        var lines = await ChatWindow.LoadGitLinesAsync(new GitService(), _repo.Root, Ct);

        Assert.Equal(new GitFileStat(path, "old.txt", GitChangeKind.Renamed, 0, 0), Assert.Single(summary.Files));
        Assert.Equal(GitPanel.Format(summary, "未提交改动"), lines);
        Assert.Equal(["未提交改动", $"{displayedPath} 0+ 0-"], lines);
    }

    [Fact]
    public async Task Refresh_TruncatesSharedStatisticsBeyondMaxRows()
    {
        _repo.Commit(Enumerable.Range(0, GitPanel.MaxRows + 3).Select(index => ($"file{index:D2}.cs", "line\n")).ToArray());
        var summary = await new GitInspector(_repo.Root).DiffSummaryAsync(GitDiffTarget.OfCommit("HEAD"), cancellationToken: Ct);

        var lines = await ChatWindow.LoadGitLinesAsync(new GitService(), _repo.Root, Ct);

        Assert.Equal(GitPanel.Format(summary, "上次提交"), lines);
        Assert.Equal(1 + GitPanel.MaxRows + 1, lines.Count);
        Assert.Equal("file00.cs 1+ 0-", lines[1]);
        Assert.Equal("~ (+3)", lines[^1]);
    }

    [Fact]
    public async Task Refresh_HandlesUnbornRepositoryAndStagedFirstCommit()
    {
        Assert.Equal(["(无变更)"], await ChatWindow.LoadGitLinesAsync(new GitService(), _repo.Root, Ct));
        _repo.Write("first.txt", "first\n");
        _repo.Git("add", "first.txt");

        Assert.Equal(["未提交改动", "first.txt 1+ 0-"], await ChatWindow.LoadGitLinesAsync(new GitService(), _repo.Root, Ct));
        _repo.Commit();
        Assert.Equal(["上次提交", "first.txt 1+ 0-"], await ChatWindow.LoadGitLinesAsync(new GitService(), _repo.Root, Ct));
        _repo.Commit();
        Assert.Equal(["(无变更)"], await ChatWindow.LoadGitLinesAsync(new GitService(), _repo.Root, Ct));
    }
}

