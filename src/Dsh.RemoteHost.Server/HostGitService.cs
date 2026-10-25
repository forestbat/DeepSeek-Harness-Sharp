using Dsh.Core;
using Dsh.GitInspect;

namespace Dsh.RemoteHost;

/** Repository operations with the host's project and worktree path boundary. */
public sealed class HostGitService(HostPathPolicy policy, IGitService git)
{
    public Task<GitStatus> StatusAsync(RemoteGitRequest request, CancellationToken cancellationToken)
        => git.StatusAsync(policy.Resolve(request.ProjectPath), cancellationToken);

    public Task<GitDiff> DiffAsync(RemoteGitDiffRequest request, CancellationToken cancellationToken)
        => git.DiffAsync(policy.Resolve(request.ProjectPath), request.Target, request.Limits, request.Paths, cancellationToken);

    public Task<IReadOnlyList<GitCommitSummary>> LogAsync(RemoteGitLogRequest request, CancellationToken cancellationToken)
        => git.LogAsync(policy.Resolve(request.ProjectPath), request.MaxCount ?? IGitService.DefaultLogCount, request.Revision, cancellationToken);

    public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(RemoteGitRequest request, CancellationToken cancellationToken)
        => git.ListWorktreesAsync(policy.Resolve(request.ProjectPath), cancellationToken);

    public Task<GitWorktree> AddWorktreeAsync(RemoteGitWorktreeAdd request, CancellationToken cancellationToken)
        => git.AddWorktreeAsync(policy.Resolve(request.ProjectPath), new GitWorktreeAddRequest(policy.Resolve(request.Path), request.NewBranch, request.StartPoint), cancellationToken);

    public Task RemoveWorktreeAsync(RemoteGitWorktreeRemove request, CancellationToken cancellationToken)
        => git.RemoveWorktreeAsync(policy.Resolve(request.ProjectPath), policy.Resolve(request.Path), request.Force, cancellationToken);
}
