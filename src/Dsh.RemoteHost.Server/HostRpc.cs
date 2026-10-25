using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/** Parameter decoding and result encoding shared by every host method handler. */
internal static class HostRpc
{
    /** JSON-RPC standard error code: invalid method parameters. */
    public const int InvalidParams = -32602;

    public static readonly JsonElement True = Serialize(true, HostProtocolJsonContext.Default.Boolean);

    public static JsonElement Serialize<T>(T value, JsonTypeInfo<T> info)
        => JsonSerializer.SerializeToElement(value, info);

    public static T Require<T>(JsonRpcMessage message, JsonTypeInfo<T> info, string error)
        => (message.Params is { } element ? JsonSerializer.Deserialize(element, info) : default)
            ?? throw new JsonRpcException(InvalidParams, error);

    public static JsonRpcException MethodNotFound(string name) => new(JsonRpcPeer.MethodNotFound, $"method not found: {name}");
}
