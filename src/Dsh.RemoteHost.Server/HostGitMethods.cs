using System.Text.Json;
using Dsh.Transport;
using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

/** host.git.* requests delegated to the configured Git domain service. */
internal static class HostGitMethods
{
    public static async ValueTask<JsonElement?> HandleAsync(HostGitService backend, string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case GitProtocol.MethodGitStatus:
            {
                var request = Require(message, GitProtocolJsonContext.Default.RemoteGitRequest, "git status requires projectPath");
                return Serialize(await backend.StatusAsync(request, cancellationToken).ConfigureAwait(false), GitProtocolJsonContext.Default.GitStatus);
            }
            case GitProtocol.MethodGitDiff:
            {
                var request = Require(message, GitProtocolJsonContext.Default.RemoteGitDiffRequest, "git diff requires projectPath and target");
                var diff = await backend.DiffAsync(request, cancellationToken).ConfigureAwait(false);
                return Serialize(diff, GitProtocolJsonContext.Default.GitDiff);
            }
            case GitProtocol.MethodGitLog:
            {
                var request = Require(message, GitProtocolJsonContext.Default.RemoteGitLogRequest, "git log requires projectPath");
                var commits = await backend.LogAsync(request, cancellationToken).ConfigureAwait(false);
                return Serialize(commits, GitProtocolJsonContext.Default.IReadOnlyListGitCommitSummary);
            }
            default:
                return await HandleWorktreeAsync(backend, name, message, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<JsonElement?> HandleWorktreeAsync(HostGitService backend, string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case GitProtocol.MethodGitWorktreeList:
            {
                var request = Require(message, GitProtocolJsonContext.Default.RemoteGitRequest, "worktree list requires projectPath");
                return Serialize(await backend.ListWorktreesAsync(request, cancellationToken).ConfigureAwait(false), GitProtocolJsonContext.Default.IReadOnlyListGitWorktree);
            }
            case GitProtocol.MethodGitWorktreeAdd:
            {
                var request = Require(message, GitProtocolJsonContext.Default.RemoteGitWorktreeAdd, "worktree add requires projectPath and path");
                return Serialize(await backend.AddWorktreeAsync(request, cancellationToken).ConfigureAwait(false), GitProtocolJsonContext.Default.GitWorktree);
            }
            case GitProtocol.MethodGitWorktreeRemove:
            {
                var request = Require(message, GitProtocolJsonContext.Default.RemoteGitWorktreeRemove, "worktree remove requires projectPath and path");
                await backend.RemoveWorktreeAsync(request, cancellationToken).ConfigureAwait(false);
                return True;
            }
            default:
                throw MethodNotFound(name);
        }
    }

}
