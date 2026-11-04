using static Dsh.RemoteHost.RemoteHostRpc;
using Dsh.GitInspect;

namespace Dsh.RemoteHost;

/** Client implementation of the optional Git domain. */
public sealed class RemoteGitClient : IWorkspaceGit
{
    private readonly RemoteHostClient _host;

    internal RemoteGitClient(RemoteHostClient host) => _host = host;

    public Task<GitStatus> GitStatusAsync(string projectPath, CancellationToken cancellationToken = default)
        => _host.SendAsync(GitProtocol.MethodGitStatus, Serialize(new RemoteGitRequest(projectPath), GitProtocolJsonContext.Default.RemoteGitRequest), GitProtocolJsonContext.Default.GitStatus, cancellationToken);

    public Task<GitDiff> GitDiffAsync(RemoteGitDiffRequest request, CancellationToken cancellationToken = default)
        => _host.SendAsync(GitProtocol.MethodGitDiff, Serialize(request, GitProtocolJsonContext.Default.RemoteGitDiffRequest), GitProtocolJsonContext.Default.GitDiff, cancellationToken);

    public Task<IReadOnlyList<GitCommitSummary>> GitLogAsync(RemoteGitLogRequest request, CancellationToken cancellationToken = default)
        => _host.SendAsync(GitProtocol.MethodGitLog, Serialize(request, GitProtocolJsonContext.Default.RemoteGitLogRequest), GitProtocolJsonContext.Default.IReadOnlyListGitCommitSummary, cancellationToken);

    public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string projectPath, CancellationToken cancellationToken = default)
        => _host.SendAsync(GitProtocol.MethodGitWorktreeList, Serialize(new RemoteGitRequest(projectPath), GitProtocolJsonContext.Default.RemoteGitRequest), GitProtocolJsonContext.Default.IReadOnlyListGitWorktree, cancellationToken);

    public Task<GitWorktree> AddWorktreeAsync(RemoteGitWorktreeAdd request, CancellationToken cancellationToken = default)
        => _host.SendAsync(GitProtocol.MethodGitWorktreeAdd, Serialize(request, GitProtocolJsonContext.Default.RemoteGitWorktreeAdd), GitProtocolJsonContext.Default.GitWorktree, cancellationToken);

    public Task RemoveWorktreeAsync(RemoteGitWorktreeRemove request, CancellationToken cancellationToken = default)
        => _host.SendAsync(GitProtocol.MethodGitWorktreeRemove, Serialize(request, GitProtocolJsonContext.Default.RemoteGitWorktreeRemove), GitProtocolJsonContext.Default.Boolean, cancellationToken);
}



