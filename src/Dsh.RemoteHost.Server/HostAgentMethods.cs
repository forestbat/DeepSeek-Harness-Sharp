using System.Text.Json;
using Dsh.Transport;
using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

/** host.agent.* requests, forwarded to the backend that owns the agent processes. */
internal static class HostAgentMethods
{
    public static async ValueTask<JsonElement?> HandleAsync(IRemoteAgentBackend backend, string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case AgentProtocol.MethodAgentCatalog:
                return Serialize(await backend.AgentCatalogAsync(cancellationToken).ConfigureAwait(false), AgentProtocolJsonContext.Default.IReadOnlyListRemoteAgentCatalogEntry);
            case AgentProtocol.MethodAgentStart:
            {
                var request = Require(message, AgentProtocolJsonContext.Default.RemoteAgentStartRequest, "agent start requires agentKind and projectPath");
                return Serialize(await backend.StartAgentAsync(request, cancellationToken).ConfigureAwait(false), AgentProtocolJsonContext.Default.RemoteSessionSummary);
            }
            case AgentProtocol.MethodAgentPrompt:
            {
                var request = Require(message, AgentProtocolJsonContext.Default.RemoteAgentPrompt, "agent prompt requires sessionId and text");
                await backend.PromptAgentAsync(request.SessionId, request.Text, cancellationToken).ConfigureAwait(false);
                return True;
            }
            case AgentProtocol.MethodAgentCancel:
                await backend.CancelAgentAsync(SessionOf(message, "agent cancel requires sessionId"), cancellationToken).ConfigureAwait(false);
                return True;
            case AgentProtocol.MethodAgentStop:
                await backend.StopAgentAsync(SessionOf(message, "agent stop requires sessionId"), cancellationToken).ConfigureAwait(false);
                return True;
            case AgentProtocol.MethodAgentResume:
                return Serialize(await backend.ResumeAgentAsync(SessionOf(message, "agent resume requires sessionId"), cancellationToken).ConfigureAwait(false), AgentProtocolJsonContext.Default.RemoteSessionSummary);
            default:
                throw MethodNotFound(name);
        }
    }

    private static string SessionOf(JsonRpcMessage message, string error)
        => Require(message, AgentProtocolJsonContext.Default.RemoteSessionRef, error).SessionId;
}
