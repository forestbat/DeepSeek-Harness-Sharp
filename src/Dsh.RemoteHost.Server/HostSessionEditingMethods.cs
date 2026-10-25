using System.Text.Json;
using Dsh.Transport;
using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

internal static class HostSessionEditingMethods
{
    public static async ValueTask<JsonElement?> HandleAsync(IRemoteSessionEditingBackend backend, string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case SessionEditingProtocol.MethodSessionCommand:
            {
                var request = Require(message, SessionEditingProtocolJsonContext.Default.RemoteCommandRequest, "command requires sessionId and line");
                return Serialize(await backend.ExecuteSessionCommandAsync(request.SessionId, request.Line, cancellationToken).ConfigureAwait(false), SessionEditingProtocolJsonContext.Default.RemoteCommandResult);
            }
            case SessionEditingProtocol.MethodSessionCommands:
            {
                var request = Require(message, SessionEditingProtocolJsonContext.Default.RemoteSessionRef, "commands requires sessionId");
                return Serialize(await backend.ListSessionCommandsAsync(request.SessionId, cancellationToken).ConfigureAwait(false), SessionEditingProtocolJsonContext.Default.IReadOnlyListRemoteCommandDescriptor);
            }
            case SessionEditingProtocol.MethodSessionRename:
            {
                var request = Require(message, SessionEditingProtocolJsonContext.Default.RemoteSessionRenameRequest, "rename requires sessionId and title");
                await backend.RenameSessionAsync(request.SessionId, request.Title, cancellationToken).ConfigureAwait(false);
                return True;
            }
            case SessionEditingProtocol.MethodSessionDelete:
                await backend.DeleteSessionAsync(Require(message, SessionEditingProtocolJsonContext.Default.RemoteSessionRef, "delete requires sessionId").SessionId, cancellationToken).ConfigureAwait(false);
                return True;
            case SessionEditingProtocol.MethodSessionApprovalPolicy:
            {
                var request = Require(message, SessionEditingProtocolJsonContext.Default.RemoteSessionPolicyRequest, "approvalPolicy requires sessionId and policy");
                await backend.SetSessionApprovalPolicyAsync(request.SessionId, request.Policy, cancellationToken).ConfigureAwait(false);
                return True;
            }
            case SessionEditingProtocol.MethodSessionSubagents:
                return Serialize(await backend.ListSubagentsAsync(Require(message, SessionEditingProtocolJsonContext.Default.RemoteSessionRef, "subagents requires sessionId").SessionId, cancellationToken).ConfigureAwait(false), SessionEditingProtocolJsonContext.Default.IReadOnlyListRemoteSubagentInfo);
            default:
                throw MethodNotFound(name);
        }
    }
}


