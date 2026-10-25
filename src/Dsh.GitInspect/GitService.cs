using Dsh.Core;

namespace Dsh.GitInspect;

/** Local Git implementation of the shared service; every call starts with a fresh repository view. */
public sealed class GitService : IGitService
{
    public Task<GitStatus> StatusAsync(string projectPath, CancellationToken cancellationToken = default)
        => new GitInspector(projectPath).StatusAsync(cancellationToken);

    public Task<GitDiff> DiffAsync(
        string projectPath,
        GitDiffTarget target,
        GitDiffLimits? limits = null,
        IReadOnlyList<string>? paths = null,
        CancellationToken cancellationToken = default)
        => new GitInspector(projectPath).DiffAsync(target, limits, paths, cancellationToken);

    public Task<GitDiffSummary> DiffSummaryAsync(
        string projectPath,
        GitDiffTarget target,
        IReadOnlyList<string>? paths = null,
        CancellationToken cancellationToken = default)
        => new GitInspector(projectPath).DiffSummaryAsync(target, paths, cancellationToken);

    public Task<IReadOnlyList<GitCommitSummary>> LogAsync(
        string projectPath,
        int maxCount,
        string? revision = null,
        CancellationToken cancellationToken = default)
        => new GitInspector(projectPath).LogAsync(maxCount, revision, cancellationToken);

    public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string projectPath, CancellationToken cancellationToken = default)
        => new GitInspector(projectPath).ListWorktreesAsync(cancellationToken);

    public Task<GitWorktree> AddWorktreeAsync(
        string projectPath,
        GitWorktreeAddRequest request,
        CancellationToken cancellationToken = default)
        => new GitInspector(projectPath).AddWorktreeAsync(request, cancellationToken);

    public Task RemoveWorktreeAsync(
        string projectPath,
        string path,
        bool force = false,
        CancellationToken cancellationToken = default)
        => new GitInspector(projectPath).RemoveWorktreeAsync(path, force, cancellationToken);
}
