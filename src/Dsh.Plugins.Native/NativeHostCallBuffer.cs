using System.Text;
using System.Text.Json;

namespace Dsh.Plugins.Native;

/// <summary>One synchronous host operation writes one JSON envelope into a caller-owned protocol frame.</summary>
public static unsafe class NativeHostCallBuffer
{
    public static int Invoke(string operation, string argumentsJson, byte* output, int capacity, Func<string, string, string> call)
    {
        // Reject probes and short buffers before a file write or HTTP request can execute.
        if (output is null || capacity < DshNativePluginAbi.MaxResultBytes
            || Encoding.UTF8.GetByteCount(operation) + (long)Encoding.UTF8.GetByteCount(argumentsJson) > DshNativePluginAbi.MaxResultBytes)
            return DshNativePluginAbi.Error;
        var destination = new Span<byte>(output, DshNativePluginAbi.MaxResultBytes);
        try
        {
            var result = call(operation, argumentsJson);
            var length = Encoding.UTF8.GetByteCount(result);
            ReadOnlySpan<byte> prefix = "{\"result\":"u8;
            if (length > destination.Length - prefix.Length - 1)
                throw new InvalidOperationException("host operation response exceeds the protocol frame");
            using var document = JsonDocument.Parse(result);
            prefix.CopyTo(destination);
            var written = Encoding.UTF8.GetBytes(result, destination[prefix.Length..]);
            destination[prefix.Length + written] = (byte)'}';
            return prefix.Length + written + 1;
        }
        catch (Exception error)
        {
            var message = JsonEncodedText.Encode(error.Message).EncodedUtf8Bytes;
            ReadOnlySpan<byte> prefix = "{\"error\":\""u8;
            if (message.Length > destination.Length - prefix.Length - 2)
                return DshNativePluginAbi.Error;
            prefix.CopyTo(destination);
            message.CopyTo(destination[prefix.Length..]);
            "\"}"u8.CopyTo(destination[(prefix.Length + message.Length)..]);
            return prefix.Length + message.Length + 2;
        }
    }
}
