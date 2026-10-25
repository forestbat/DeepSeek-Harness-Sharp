using static Dsh.RemoteHost.RemoteHostRpc;

namespace Dsh.RemoteHost;

/** Client implementation of the optional SessionEditing domain. */
public sealed class RemoteSessionEditingClient
{
    private readonly RemoteHostClient _host;

    internal RemoteSessionEditingClient(RemoteHostClient host) => _host = host;

    public Task<RemoteCommandResult> ExecuteSessionCommandAsync(string sessionId, string line, CancellationToken cancellationToken = default)
        => _host.SendAsync(SessionEditingProtocol.MethodSessionCommand, Serialize(new RemoteCommandRequest(sessionId, line), SessionEditingProtocolJsonContext.Default.RemoteCommandRequest), SessionEditingProtocolJsonContext.Default.RemoteCommandResult, cancellationToken);

    public Task<IReadOnlyList<RemoteCommandDescriptor>> ListSessionCommandsAsync(string sessionId, CancellationToken cancellationToken = default)
        => _host.SendAsync(SessionEditingProtocol.MethodSessionCommands, Serialize(new RemoteSessionRef(sessionId), SessionEditingProtocolJsonContext.Default.RemoteSessionRef), SessionEditingProtocolJsonContext.Default.IReadOnlyListRemoteCommandDescriptor, cancellationToken);

    public Task RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken = default)
        => _host.SendAsync(SessionEditingProtocol.MethodSessionRename, Serialize(new RemoteSessionRenameRequest(sessionId, title), SessionEditingProtocolJsonContext.Default.RemoteSessionRenameRequest), SessionEditingProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => _host.SendAsync(SessionEditingProtocol.MethodSessionDelete, Serialize(new RemoteSessionRef(sessionId), SessionEditingProtocolJsonContext.Default.RemoteSessionRef), SessionEditingProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task SetSessionApprovalPolicyAsync(string sessionId, string policy, CancellationToken cancellationToken = default)
        => _host.SendAsync(SessionEditingProtocol.MethodSessionApprovalPolicy, Serialize(new RemoteSessionPolicyRequest(sessionId, policy), SessionEditingProtocolJsonContext.Default.RemoteSessionPolicyRequest), SessionEditingProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task<IReadOnlyList<RemoteSubagentInfo>> ListSubagentsAsync(string rootSessionId, CancellationToken cancellationToken = default)
        => _host.SendAsync(SessionEditingProtocol.MethodSessionSubagents, Serialize(new RemoteSessionRef(rootSessionId), SessionEditingProtocolJsonContext.Default.RemoteSessionRef), SessionEditingProtocolJsonContext.Default.IReadOnlyListRemoteSubagentInfo, cancellationToken);
}



