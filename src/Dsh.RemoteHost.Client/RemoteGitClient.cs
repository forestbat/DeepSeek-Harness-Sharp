using Dsh.GitInspect;
using Dsh.RemoteHost.Rpc;

namespace Dsh.RemoteHost;

/** Client implementation of the optional Git domain. */
public sealed class RemoteGitClient : IWorkspaceGit
{
    private readonly IGitRpc _rpc;

    internal RemoteGitClient(RemoteHostClient host, IGitRpc rpc) => _rpc = rpc;

    public Task<GitStatus> GitStatusAsync(string projectPath, CancellationToken cancellationToken = default)
        => _rpc.StatusAsync(new RemoteGitRequest(projectPath), cancellationToken);

    public Task<GitDiff> GitDiffAsync(RemoteGitDiffRequest request, CancellationToken cancellationToken = default)
        => _rpc.DiffAsync(request, cancellationToken);

    public Task<IReadOnlyList<GitCommitSummary>> GitLogAsync(RemoteGitLogRequest request, CancellationToken cancellationToken = default)
        => _rpc.LogAsync(request, cancellationToken);

    public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string projectPath, CancellationToken cancellationToken = default)
        => _rpc.ListWorktreesAsync(new RemoteGitRequest(projectPath), cancellationToken);

    public Task<GitWorktree> AddWorktreeAsync(RemoteGitWorktreeAdd request, CancellationToken cancellationToken = default)
        => _rpc.AddWorktreeAsync(request, cancellationToken);

    public async Task RemoveWorktreeAsync(RemoteGitWorktreeRemove request, CancellationToken cancellationToken = default)
        => await _rpc.RemoveWorktreeAsync(request, cancellationToken).ConfigureAwait(false);
}
