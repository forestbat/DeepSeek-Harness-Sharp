using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dsh.GitInspect;
using Dsh.Gui.Services;
using Dsh.Interaction;
using Dsh.RemoteHost;

namespace Dsh.Gui.ViewModels;

/**
 * 一台机器上某个项目的只读 git 视图, 经 IWorkspaceGit 的 host.git.* 完成(未跟踪文件经 IWorkspaceFiles 读内容)。
 * 用法: new GitViewModel(git, files, projectPath, style) 后 await RefreshAsync(); 选中文件或提交即在 DiffCards 里给出 diff。
 * 只做查看与 worktree 管理, 不做 stage/commit/push 等写操作。
 * 新建 worktree 后与 worktree 行上的"新建会话"都经 NewSessionRequested 交给集成方去走新建会话流程。
 */
public sealed partial class GitViewModel(IWorkspaceGit git, IWorkspaceFiles files, string projectPath, RemotePathStyle style) : OperationViewModel
{
    public const int LogCount = 50;

    /** DiffView 每行一个控件, 一次最多渲染这么多行, 其余提示截断。 */
    public const int MaxRenderedDiffLines = 4000;

    private const int ShortShaLength = 7;

    private readonly RemotePathStyle _style = style;
    private string? _repositoryRoot;
    private int _diffVersion;

    public string ProjectPath => projectPath;

    /** 包含项目目录的 worktree 根目录(刷新后才知道, 之前按项目目录本身)。 */
    private string RepositoryRoot => _repositoryRoot ?? projectPath;

    [ObservableProperty]
    private string _branchSummary = "";

    public ObservableCollection<GitChangeGroupViewModel> Groups { get; } = [];

    public ObservableCollection<GitCommitSummary> Commits { get; } = [];

    public ObservableCollection<GitWorktree> Worktrees { get; } = [];

    public ObservableCollection<DiffCard> DiffCards { get; } = [];

    public GitChangeItemViewModel? SelectedChange { get; private set; }

    [ObservableProperty]
    private GitCommitSummary? _selectedCommit;

    [ObservableProperty]
    private string? _diffTitle;

    /** 截断、二进制、无文本改动等说明, 每条一行。 */
    [ObservableProperty]
    private string? _diffNotice;

    [ObservableProperty]
    private string _newWorktreePath = "";

    [ObservableProperty]
    private string _newWorktreeBranch = "";

    /** 移除 worktree 时连同其中未提交的修改一起丢弃(git worktree remove --force)。 */
    [ObservableProperty]
    private bool _forceRemove;

    /** 请求在某个 worktree 里新建会话(集成阶段接到新建会话流程)。 */
    public event Action<GitWorktree>? NewSessionRequested;

    [RelayCommand]
    public Task<bool> RefreshAsync() => RunAsync(async () =>
    {
        var status = git.GitStatusAsync(projectPath);
        var worktrees = git.ListWorktreesAsync(projectPath);
        var commits = git.GitLogAsync(new RemoteGitLogRequest(projectPath, LogCount));
        var project = files.StatAsync(projectPath);
        await Task.WhenAll(status, worktrees, commits, project);
        ApplyWorktrees(await worktrees, (await project).Path);
        ApplyStatus(await status);
        Replace(Commits, await commits);
    });

    /** 选中状态列表里的文件: 已暂存看 HEAD↔暂存区, 未暂存与冲突看暂存区↔工作区, 未跟踪直接读文件内容。 */
    [RelayCommand]
    public Task<bool> SelectChangeAsync(GitChangeItemViewModel item)
    {
        SelectedChange?.IsSelected = false;
        (SelectedChange, item.IsSelected) = (item, true);
        SelectedCommit = null;
        var version = ++_diffVersion;
        return RunAsync(() => item.Section switch
        {
            GitChangeSection.Untracked => ShowUntrackedAsync(item, version),
            GitChangeSection.Staged => ShowDiffAsync(version, item.Label, new RemoteGitDiffRequest(projectPath, GitDiffTarget.Staged, [AbsolutePath(item.Path)])),
            _ => ShowDiffAsync(version, item.Label, new RemoteGitDiffRequest(projectPath, GitDiffTarget.WorkingTree, [AbsolutePath(item.Path)])),
        });
    }

    partial void OnSelectedCommitChanged(GitCommitSummary? value)
    {
        if (value is null)
            return;
        SelectedChange?.IsSelected = false;
        SelectedChange = null;
        var version = ++_diffVersion;
        _ = RunAsync(() => ShowDiffAsync(version, $"{value.ShortSha} {value.Subject}", new RemoteGitDiffRequest(projectPath, GitDiffTarget.OfCommit(value.Sha))));
    }

    /** 新建 worktree: 相对路径以仓库根目录为基准; 分支名留空时按 git worktree add 的默认行为, 以目录名新建分支。 */
    [RelayCommand]
    private Task AddWorktreeAsync() => RunAsync(async () =>
    {
        var input = NewWorktreePath.Trim();
        if (input.Length == 0)
            throw new ArgumentException("请输入 worktree 路径");
        var branch = NewWorktreeBranch.Trim();
        var request = new RemoteGitWorktreeAdd(projectPath, RemotePaths.Resolve(RepositoryRoot, input, _style), branch.Length == 0 ? null : branch);
        var added = await git.AddWorktreeAsync(request);
        (NewWorktreePath, NewWorktreeBranch) = ("", "");
        Replace(Worktrees, await git.ListWorktreesAsync(projectPath));
        Report($"已新建 worktree {added.Path}");
        Ask($"已新建 worktree {added.Path}（{added.Branch ?? "分离 HEAD"}）。要在这里新建会话吗？", "新建会话", () => RequestSession(added));
    });

    [RelayCommand]
    private Task RemoveWorktreeAsync(GitWorktree worktree)
    {
        if (worktree.IsMain)
        {
            Report("主 worktree 不能移除", isError: true);
            return Task.CompletedTask;
        }

        var force = ForceRemove;
        var consequence = force ? "其中未提交的修改会一并丢弃" : "有未提交修改时 git 会拒绝移除";
        Ask($"移除 worktree {worktree.Path}？{consequence}。", "移除", () => RunAsync(async () =>
        {
            await git.RemoveWorktreeAsync(new RemoteGitWorktreeRemove(projectPath, worktree.Path, force));
            Replace(Worktrees, await git.ListWorktreesAsync(projectPath));
        }, $"已移除 worktree {worktree.Path}"));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task RequestSession(GitWorktree worktree)
    {
        NewSessionRequested?.Invoke(worktree);
        return Task.CompletedTask;
    }

    private void ApplyStatus(GitStatus status)
    {
        var branch = status.Branch;
        var head = branch.Head ?? $"分离 HEAD {branch.Oid?[..Math.Min(branch.Oid.Length, ShortShaLength)] ?? ""}";
        var tracking = branch.Upstream is null ? "无上游" : $"↑{branch.Ahead ?? 0} ↓{branch.Behind ?? 0} 相对 {branch.Upstream}";
        BranchSummary = $"{head} · {tracking}";
        Replace(Groups,
        [
            Group(GitChangeSection.Staged, "已暂存", status.Staged),
            Group(GitChangeSection.Unstaged, "未暂存", status.Unstaged),
            new GitChangeGroupViewModel(GitChangeSection.Untracked, "未跟踪", [.. status.Untracked.Select(path => new GitChangeItemViewModel(GitChangeSection.Untracked, path, null, null))]),
            Group(GitChangeSection.Conflicted, "冲突", status.Conflicted),
        ]);
        SelectedChange = null;
    }

    private static GitChangeGroupViewModel Group(GitChangeSection section, string title, IReadOnlyList<GitFileChange> changes)
        => new(section, title, [.. changes.Select(change => new GitChangeItemViewModel(section, change.Path, change.Kind, change.OriginalPath))]);

    /** 仓库根目录 = 包含项目目录的 worktree 中路径最长的一个; 都不包含(例如经符号链接打开)时退回项目目录本身。 */
    private void ApplyWorktrees(IReadOnlyList<GitWorktree> worktrees, string resolvedProject)
    {
        Replace(Worktrees, worktrees);
        var trimmed = resolvedProject.TrimEnd('/', '\\');
        _repositoryRoot = worktrees
            .Select(worktree => worktree.Path.TrimEnd('/', '\\'))
            .Where(root => trimmed == root || (trimmed.StartsWith(root, StringComparison.Ordinal) && trimmed[root.Length] is '/' or '\\'))
            .MaxBy(root => root.Length) ?? resolvedProject;
    }

    /** git 的状态路径相对仓库根, diff 的路径参数相对项目目录解析, 所以统一换成绝对路径。 */
    private string AbsolutePath(string repositoryRelative) => RemotePaths.Resolve(RepositoryRoot, repositoryRelative.TrimEnd('/'), _style);

    private async Task ShowDiffAsync(int version, string title, RemoteGitDiffRequest request)
    {
        var diff = await git.GitDiffAsync(request);
        if (version != _diffVersion)
            return;
        var notices = new List<string>();
        var cards = diff.Files.Select(file => CardOf(file, notices)).OfType<DiffCard>().ToList();
        if (diff.Files.Count == 0)
            notices.Add("没有差异");
        ShowCards(title, cards, notices);
    }

    private static DiffCard? CardOf(GitFileDiff file, List<string> notices)
    {
        var title = file.OriginalPath is null ? file.Path : $"{file.OriginalPath} → {file.Path}";
        if (file.Truncated)
            notices.Add($"{title}: diff 过大，已被 host 截断");
        if (file.IsBinary)
        {
            notices.Add($"{title}: 二进制文件");
            return null;
        }

        var card = DiffCardExtractor.FromUnifiedDiff(title, file.Patch);
        if (card is null && !file.Truncated)
            notices.Add($"{title}: 没有文本改动（{file.Kind}）");
        return card;
    }

    private async Task ShowUntrackedAsync(GitChangeItemViewModel item, int version)
    {
        var path = AbsolutePath(item.Path);
        var stat = await files.StatAsync(path);
        var text = stat is { Exists: true, IsDirectory: false } && stat.Size <= FilesViewModel.MaxEditableBytes
            ? TextFileContent.TryDecode(await files.ReadFileAsync(path))?.Text
            : null;
        if (version != _diffVersion)
            return;
        var title = $"{item.Path}（未跟踪）";
        if (text is not null)
            ShowCards(title, [new DiffCard(title, "untracked", DiffCardExtractor.DiffLines(null, text))], []);
        else
            ShowCards(title, [], [stat.IsDirectory ? "未跟踪的目录" : $"二进制或超过 {FileSizeText.Format(FilesViewModel.MaxEditableBytes)} 的文件，不显示内容"]);
    }

    /** 渲染行数超过上限时截断后面的卡片行。 */
    private void ShowCards(string title, IReadOnlyList<DiffCard> cards, List<string> notices)
    {
        var budget = MaxRenderedDiffLines;
        DiffCards.Clear();
        foreach (var card in cards)
        {
            if (budget <= 0)
                break;
            DiffCards.Add(card.Lines.Count <= budget ? card : new DiffCard(card.Title, card.Source, [.. card.Lines.Take(budget)]));
            budget -= card.Lines.Count;
        }

        if (budget < 0 || DiffCards.Count < cards.Count)
            notices.Add($"只显示前 {MaxRenderedDiffLines} 行");
        DiffTitle = title;
        DiffNotice = notices.Count == 0 ? null : string.Join(Environment.NewLine, notices);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }
}

