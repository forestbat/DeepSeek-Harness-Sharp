using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Dsh.Lsp;

/// <summary>Content-Length framing for LSP messages carried by the existing Host stdio channel.</summary>
internal static class LspStdioTransport
{
    private const int MaxHeaderBytes = 8192;
    private const int MaxContentBytes = 16 * 1024 * 1024;

    public static async Task<int> RunAsync(Stream input, Stream output,
        Func<string, JsonElement?, Task<object?>> handler, CancellationToken cancellationToken = default)
    {
        var header = new byte[MaxHeaderBytes];
        var shutdown = false;
        while (true)
        {
            var length = await ReadLengthAsync(input, header, cancellationToken).ConfigureAwait(false);
            if (length is null) return shutdown ? 0 : 1;
            var payload = new byte[length.Value];
            await input.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);
            var request = document.RootElement;
            if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("method", out var methodValue))
                throw new IOException("LSP request requires a method.");
            var method = methodValue.GetString() ?? throw new IOException("LSP method must be a string.");
            if (method == "exit") return shutdown ? 0 : 1;
            if (!request.TryGetProperty("id", out var id)) continue;
            object? result = null;
            string? error = null;
            try
            {
                if (shutdown) throw new InvalidOperationException("The LSP server has shut down.");
                result = await handler(method, request.TryGetProperty("params", out var parameters) ? parameters : null).ConfigureAwait(false);
                if (method == "shutdown") shutdown = true;
            }
            catch (InvalidOperationException exception) { error = exception.Message; }
            var response = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(response))
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WritePropertyName("id"); id.WriteTo(writer);
                if (error is null) { writer.WritePropertyName("result"); WriteValue(writer, result); }
                else
                {
                    writer.WritePropertyName("error"); writer.WriteStartObject();
                    writer.WriteNumber("code", -32601); writer.WriteString("message", error); writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            var responseHeader = Encoding.ASCII.GetBytes("Content-Length: " + response.WrittenCount.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
            await output.WriteAsync(responseHeader, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(response.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int?> ReadLengthAsync(Stream input, byte[] header, CancellationToken cancellationToken)
    {
        var count = 0;
        while (true)
        {
            if (count == header.Length) throw new IOException("LSP header exceeds its limit.");
            var read = await input.ReadAsync(header.AsMemory(count, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (count == 0) return null;
                throw new EndOfStreamException("LSP header ended prematurely.");
            }
            count++;
            if (count >= 4 && header[count - 4] == '\r' && header[count - 3] == '\n' && header[count - 2] == '\r' && header[count - 1] == '\n') break;
        }
        int? length = null;
        foreach (var line in Encoding.ASCII.GetString(header, 0, count - 4).Split("\r\n", StringSplitOptions.None))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) throw new IOException("Invalid LSP header.");
            if (!line[..separator].Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (length is not null || !int.TryParse(line.AsSpan(separator + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0 || value > MaxContentBytes)
                throw new IOException("Invalid LSP Content-Length.");
            length = value;
        }
        return length ?? throw new IOException("LSP Content-Length is required.");
    }

    // The existing LSP service returns dictionaries of primitives. Avoid reflection-based object serialization in trimmed builds.
    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string text: writer.WriteStringValue(text); break;
            case bool boolean: writer.WriteBooleanValue(boolean); break;
            case int number: writer.WriteNumberValue(number); break;
            case JsonElement element: element.WriteTo(writer); break;
            case IReadOnlyDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var pair in map) { writer.WritePropertyName(pair.Key); WriteValue(writer, pair.Value); }
                writer.WriteEndObject();
                break;
            case IEnumerable<object?> values:
                writer.WriteStartArray(); foreach (var item in values) WriteValue(writer, item); writer.WriteEndArray(); break;
            default: throw new NotSupportedException("The LSP service returned an unsupported response type.");
        }
    }
}
