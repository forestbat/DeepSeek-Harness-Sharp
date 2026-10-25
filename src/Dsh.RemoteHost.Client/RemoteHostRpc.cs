using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Dsh.RemoteHost;

internal static class RemoteHostRpc
{
    public static JsonElement Serialize<T>(T value, JsonTypeInfo<T> info)
        => JsonSerializer.SerializeToElement(value, info);
}

