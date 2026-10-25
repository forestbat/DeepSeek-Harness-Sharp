using Dsh.GitInspect;

namespace Dsh.GitInspect
{

public enum GitDiffSource
{
    WorkingTree,
    Staged,
    Commit,
    Uncommitted,
}

public sealed record GitDiffTarget(GitDiffSource Source, string? Commit = null)
{
    public static GitDiffTarget WorkingTree { get; } = new(GitDiffSource.WorkingTree);
    public static GitDiffTarget Staged { get; } = new(GitDiffSource.Staged);
    public static GitDiffTarget Uncommitted { get; } = new(GitDiffSource.Uncommitted);
    public static GitDiffTarget OfCommit(string commit) => new(GitDiffSource.Commit, commit);
}

public sealed record GitDiffLimits(int MaxPatchChars, int MaxTotalChars)
{
    public const int DefaultMaxPatchChars = 256 * 1024;
    public const int DefaultMaxTotalChars = 4 * 1024 * 1024;
    public static GitDiffLimits Default { get; } = new(DefaultMaxPatchChars, DefaultMaxTotalChars);
}

public sealed record GitFileDiff(
    string Path,
    string? OriginalPath,
    GitChangeKind Kind,
    int? Added,
    int? Deleted,
    string Patch,
    bool Truncated)
{
    public bool IsBinary => Added is null;
}

public sealed record GitDiff(GitDiffTarget Target, IReadOnlyList<GitFileDiff> Files, bool Truncated);

public sealed record GitFileStat(string Path, string? OriginalPath, GitChangeKind Kind, int? Added, int? Deleted)
{
    public bool IsBinary => Added is null;
}

public sealed record GitDiffSummary(GitDiffTarget Target, IReadOnlyList<GitFileStat> Files);

public enum GitChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Unmerged,
}

public sealed record GitFileChange(string Path, GitChangeKind Kind, string? OriginalPath = null);

public sealed record GitBranchStatus(string? Head, string? Oid, string? Upstream, int? Ahead, int? Behind)
{
    public bool IsDetached => Head is null;
}

public sealed record GitStatus(
    GitBranchStatus Branch,
    IReadOnlyList<GitFileChange> Staged,
    IReadOnlyList<GitFileChange> Unstaged,
    IReadOnlyList<string> Untracked,
    IReadOnlyList<GitFileChange> Conflicted)
{
    public bool IsClean => Staged.Count == 0 && Unstaged.Count == 0 && Untracked.Count == 0 && Conflicted.Count == 0;
}

public sealed record GitCommitSummary(
    string Sha,
    string ShortSha,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    string Subject);

/** One entry of `git worktree list --porcelain`; IsMain marks the main working tree (always listed first). */
public sealed record GitWorktree(
    string Path,
    string? Head,
    string? Branch,
    bool IsMain,
    bool IsBare,
    bool IsDetached,
    bool IsLocked,
    bool IsPrunable);

/**
 * Request for `git worktree add`.
 * NewBranch creates a branch at StartPoint (HEAD when null); without NewBranch, StartPoint may name an existing branch to check out.
 */
public sealed record GitWorktreeAddRequest(string Path, string? NewBranch = null, string? StartPoint = null);
}

namespace Dsh.Core
{
/** Local and host-side Git inspection share this service; implementations may be replaced by a plugin. */
public interface IGitService
{
    public const string ServiceName = "git";
    public const int DefaultLogCount = 50;

    Task<GitStatus> StatusAsync(string projectPath, CancellationToken cancellationToken = default);

    Task<GitDiff> DiffAsync(
        string projectPath,
        GitDiffTarget target,
        GitDiffLimits? limits = null,
        IReadOnlyList<string>? paths = null,
        CancellationToken cancellationToken = default);

    Task<GitDiffSummary> DiffSummaryAsync(
        string projectPath,
        GitDiffTarget target,
        IReadOnlyList<string>? paths = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitCommitSummary>> LogAsync(
        string projectPath,
        int maxCount,
        string? revision = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string projectPath, CancellationToken cancellationToken = default);

    Task<GitWorktree> AddWorktreeAsync(
        string projectPath,
        GitWorktreeAddRequest request,
        CancellationToken cancellationToken = default);

    Task RemoveWorktreeAsync(
        string projectPath,
        string path,
        bool force = false,
        CancellationToken cancellationToken = default);
}
}
