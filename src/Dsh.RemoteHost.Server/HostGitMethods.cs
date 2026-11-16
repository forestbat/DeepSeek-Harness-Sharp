using Dsh.GitInspect;
using Dsh.RemoteHost.Rpc;

namespace Dsh.RemoteHost;

/** host.git.* requests delegated to the configured Git domain service. */
internal sealed class HostGitMethods(HostGitService backend, Action ensureAuthenticated) : IGitRpc
{
    public async Task<GitStatus> StatusAsync(RemoteGitRequest request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        return await backend.StatusAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitDiff> DiffAsync(RemoteGitDiffRequest request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        return await backend.DiffAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitCommitSummary>> LogAsync(RemoteGitLogRequest request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        return await backend.LogAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(RemoteGitRequest request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        return await backend.ListWorktreesAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitWorktree> AddWorktreeAsync(RemoteGitWorktreeAdd request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        return await backend.AddWorktreeAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RemoveWorktreeAsync(RemoteGitWorktreeRemove request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        await backend.RemoveWorktreeAsync(request, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
