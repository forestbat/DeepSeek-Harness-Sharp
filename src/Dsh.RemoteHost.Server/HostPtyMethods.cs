using System.Text.Json;
using Dsh.Transport;
using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

/** Connection-local PTY RPC adapter. The connection owns subscription pumps, not PTY lifetimes. */
internal sealed class HostPtyMethods(
    IRemotePtyBackend backend,
    Func<string, string, Task> attach,
    Action<string> detach)
{
    /** Decode and register reader ownership in receive order; acquisition continues off the frame reader. */
    public ValueTask<JsonElement?>? Prepare(string name, JsonRpcMessage message)
    {
        switch (name)
        {
            case PtyProtocol.MethodPtyAttach:
            {
                var request = Require(message, PtyProtocolJsonContext.Default.RemotePtyAttachRequest, "pty attach requires ptyId and subscriptionId");
                return AttachAsync(request);
            }
            case PtyProtocol.MethodPtyDetach:
                detach(Require(message, PtyProtocolJsonContext.Default.RemoteSubscriptionRef, "pty detach requires subscriptionId").SubscriptionId);
                return ValueTask.FromResult<JsonElement?>(True);
            default:
                return null;
        }
    }

    private async ValueTask<JsonElement?> AttachAsync(RemotePtyAttachRequest request)
    {
        await attach(request.SubscriptionId, request.PtyId).ConfigureAwait(false);
        return True;
    }

    public async ValueTask<JsonElement?> HandleAsync(string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case PtyProtocol.MethodPtyStart:
            {
                var request = Require(message, PtyProtocolJsonContext.Default.RemotePtyStartRequest, "pty start requires fileName");
                var ptyId = await backend.StartPtyAsync(request, cancellationToken).ConfigureAwait(false);
                return Serialize(new RemotePtyRef(ptyId), PtyProtocolJsonContext.Default.RemotePtyRef);
            }
            case PtyProtocol.MethodPtyWrite:
            {
                var request = Require(message, PtyProtocolJsonContext.Default.RemotePtyInput, "pty write requires ptyId");
                await backend.WritePtyAsync(request.PtyId, Convert.FromBase64String(request.Base64), cancellationToken).ConfigureAwait(false);
                return True;
            }
            case PtyProtocol.MethodPtyResize:
            {
                var request = Require(message, PtyProtocolJsonContext.Default.RemotePtyResize, "pty resize requires ptyId, columns and rows");
                await backend.ResizePtyAsync(request.PtyId, request.Columns, request.Rows, cancellationToken).ConfigureAwait(false);
                return True;
            }
            case PtyProtocol.MethodPtyStop:
                await backend.StopPtyAsync(Require(message, PtyProtocolJsonContext.Default.RemotePtyRef, "pty stop requires ptyId").PtyId, cancellationToken).ConfigureAwait(false);
                return True;
            default:
                throw MethodNotFound(name);
        }
    }
}


