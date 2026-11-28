using System.Buffers;
using System.Globalization;
using System.Text;

namespace Dsh.Transport;

/** Thrown when an incoming HTTP request body exceeds the configured size limit. */
public sealed class HttpBodyTooLargeException(string message) : Exception(message);

/** Parsed HTTP/1.1 request head plus any body bytes already buffered past the header terminator. */
public sealed record HttpRequestHead(
    string Method,
    string Path,
    string Query,
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> InitialBody,
    bool IsChunked,
    long? ContentLength)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name);
}

/** One parsed HTTP/1.1 request: method, path without query, the raw query (without '?'), headers (case-insensitive, repeated ones joined by ", ") and the body. */
public sealed record HttpRequest(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name);

    /** The decoded value of the first `name=value` query parameter, or null when the query has none. */
    public string? QueryValue(string name)
    {
        foreach (var pair in Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals > 0 && pair.AsSpan(0, equals).SequenceEqual(name))
                return Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
        }

        return null;
    }
}

/**
 * Shared HTTP/1.1 request reader for socket-based servers.
 * Reads a bounded head and a Content-Length body, with optional chunked framing for streaming HTTP clients.
 */
public static class HttpRequestReader
{
    /** Upper bound of request line plus headers; browsers stay far below it. */
    public const int MaxHeadBytes = 16 * 1024;

    /** Default body limit for small browser forms; protocol hosts can supply their own limit. */
    public const int MaxBodyBytes = 4 * 1024;

    private const int MaxHeaderLines = 100;
    private const int IncrementalReadChunkBytes = 64 * 1024;

    private static readonly byte[] HeadTerminator = "\r\n\r\n"u8.ToArray();
    private static readonly byte[] ContinueResponse = "HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray();

    /** Returns null when the client closed the connection before sending anything. */
    public static async Task<HttpRequest?> ReadAsync(Stream stream, CancellationToken cancellationToken, int maxBodyBytes = MaxBodyBytes, bool allowChunked = false, bool allowExpectContinue = false)
    {
        var head = await ReadHeadAsync(stream, cancellationToken).ConfigureAwait(false);
        if (head is null)
            return null;
        var body = await ReadBodyAsync(stream, head, maxBodyBytes, allowChunked, allowExpectContinue, cancellationToken).ConfigureAwait(false);
        return new HttpRequest(head.Method, head.Path, head.Query, head.Headers, body);
    }

    /** Reads only the HTTP request line and headers so callers can authenticate and route before touching the body. */
    public static async Task<HttpRequestHead?> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHeadBytes];
        var (filled, headLength) = await ReadHeadBytesAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        if (filled == 0)
            return null;
        var lines = Encoding.Latin1.GetString(buffer, 0, headLength).Split("\r\n");
        var (method, path, query) = ParseRequestLine(lines[0]);
        var headers = ParseHeaders(lines.AsSpan(1));
        var bodyOffset = headLength + HeadTerminator.Length;
        var initialBody = buffer.AsMemory(bodyOffset, filled - bodyOffset);
        var isChunked = headers.TryGetValue("Transfer-Encoding", out var encoding)
            && string.Equals(encoding, "chunked", StringComparison.OrdinalIgnoreCase);
        long? contentLength = headers.TryGetValue("Content-Length", out var declared)
            && long.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLength)
            ? parsedLength
            : null;
        return new HttpRequestHead(method, path, query, headers, initialBody, isChunked, contentLength);
    }

    /** Reads the body described by a previously parsed head up to maxBodyBytes. */
    public static async Task<byte[]> ReadBodyAsync(
        Stream stream,
        HttpRequestHead head,
        int maxBodyBytes = MaxBodyBytes,
        bool allowChunked = false,
        bool allowExpectContinue = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBodyBytes);
        if (head.Headers.TryGetValue("Transfer-Encoding", out var encoding))
        {
            if (!allowChunked || !string.Equals(encoding, "chunked", StringComparison.OrdinalIgnoreCase) || head.Headers.ContainsKey("Content-Length"))
                throw new InvalidDataException("unsupported or ambiguous request body framing");
            await WriteContinueAsync(stream, head.Headers, allowExpectContinue, cancellationToken).ConfigureAwait(false);
            return await new ChunkedBodyReader(stream, head.InitialBody).ReadAsync(maxBodyBytes, cancellationToken).ConfigureAwait(false);
        }
        if (!head.Headers.TryGetValue("Content-Length", out var declared))
            return [];
        if (!long.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out var declaredLength))
            throw new InvalidDataException("request body missing a valid Content-Length within the limit");
        if (declaredLength > maxBodyBytes)
            throw new HttpBodyTooLargeException($"request body length {declaredLength} exceeds limit of {maxBodyBytes} bytes");
        var length = (int)declaredLength;
        if (length == 0)
            return [];
        await WriteContinueAsync(stream, head.Headers, allowExpectContinue, cancellationToken).ConfigureAwait(false);
        return await ReadFixedLengthBodyAsync(stream, head.InitialBody, length, cancellationToken).ConfigureAwait(false);
    }

    /** Fills the buffer until the blank line that ends the head; returns the bytes read and the head's length without its terminator. */
    private static async Task<(int Filled, int HeadLength)> ReadHeadBytesAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return filled == 0 ? (0, 0) : throw new InvalidDataException("connection closed inside the request head");
            filled += read;
            var end = buffer.AsSpan(0, filled).IndexOf(HeadTerminator);
            if (end >= 0)
                return (filled, end);
        }

        throw new InvalidDataException("request head too large");
    }

    private static async Task<byte[]> ReadFixedLengthBodyAsync(
        Stream stream,
        ReadOnlyMemory<byte> initialBody,
        int length,
        CancellationToken cancellationToken)
    {
        using var body = new MemoryStream(Math.Min(length, IncrementalReadChunkBytes));
        var copied = Math.Min(length, initialBody.Length);
        if (copied > 0)
            body.Write(initialBody.Span[..copied]);
        var remaining = length - copied;
        if (remaining > 0)
        {
            var chunk = ArrayPool<byte>.Shared.Rent(Math.Min(remaining, IncrementalReadChunkBytes));
            try
            {
                while (remaining > 0)
                {
                    var toRead = Math.Min(remaining, chunk.Length);
                    var read = await stream.ReadAsync(chunk.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        throw new EndOfStreamException("connection closed inside the request body");
                    body.Write(chunk, 0, read);
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }
        return body.ToArray();
    }

    private static (string Method, string Path, string Query) ParseRequestLine(string line)
    {
        var parts = line.Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || !parts[1].StartsWith('/'))
            throw new InvalidDataException("malformed request line");
        var query = parts[1].IndexOf('?');
        return query >= 0 ? (parts[0], parts[1][..query], parts[1][(query + 1)..]) : (parts[0], parts[1], "");
    }

    private static Dictionary<string, string> ParseHeaders(ReadOnlySpan<string> lines)
    {
        if (lines.Length > MaxHeaderLines)
            throw new InvalidDataException("too many request headers");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                throw new InvalidDataException("malformed request header");
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var previous) ? $"{previous}, {value}" : value;
        }

        return headers;
    }

    /** Only acknowledge a supported expectation after the request's framing and declared size have been accepted. */
    private static async Task WriteContinueAsync(Stream stream, IReadOnlyDictionary<string, string> headers, bool allowed, CancellationToken cancellationToken)
    {
        if (!allowed || !headers.TryGetValue("Expect", out var expectation)
            || !string.Equals(expectation, "100-continue", StringComparison.OrdinalIgnoreCase))
            return;
        await stream.WriteAsync(ContinueResponse, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class ChunkedBodyReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[MaxHeadBytes];
        private int _offset;
        private int _filled;

        public ChunkedBodyReader(Stream stream, ReadOnlyMemory<byte> initialBody)
        {
            _stream = stream;
            if (!initialBody.IsEmpty)
            {
                initialBody.CopyTo(_buffer);
                _filled = initialBody.Length;
            }
        }

        public async Task<byte[]> ReadAsync(int maxBodyBytes, CancellationToken cancellationToken)
        {
            using var body = new MemoryStream();
            while (true)
            {
                var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
                var extension = line.IndexOf(';');
                var size = extension < 0 ? line.AsSpan() : line.AsSpan(0, extension);
                if (!int.TryParse(size, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var length)
                    || length < 0)
                    throw new InvalidDataException("invalid chunk size");
                if (length > maxBodyBytes - body.Length)
                    throw new HttpBodyTooLargeException($"chunked request body exceeds limit of {maxBodyBytes} bytes");
                if (length == 0)
                {
                    var trailerBytes = 0;
                    while ((line = await ReadLineAsync(cancellationToken).ConfigureAwait(false)).Length != 0)
                    {
                        trailerBytes += line.Length + 2;
                        if (trailerBytes > MaxHeadBytes || line.IndexOf(':') <= 0)
                            throw new InvalidDataException("invalid request trailers");
                    }
                    return body.ToArray();
                }
                while (length > 0)
                {
                    await FillAsync(cancellationToken).ConfigureAwait(false);
                    var count = Math.Min(length, _filled - _offset);
                    body.Write(_buffer, _offset, count);
                    _offset += count;
                    length -= count;
                }
                if ((await ReadLineAsync(cancellationToken).ConfigureAwait(false)).Length != 0)
                    throw new InvalidDataException("missing chunk terminator");
            }
        }

        private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            while (line.Length < MaxHeadBytes)
            {
                await FillAsync(cancellationToken).ConfigureAwait(false);
                var value = _buffer[_offset++];
                if (value == '\r')
                {
                    await FillAsync(cancellationToken).ConfigureAwait(false);
                    if (_buffer[_offset++] != '\n')
                        throw new InvalidDataException("malformed chunk line");
                    return line.ToString();
                }
                if (value == '\n')
                    throw new InvalidDataException("malformed chunk line");
                line.Append((char)value);
            }
            throw new InvalidDataException("chunk line too large");
        }

        private async Task FillAsync(CancellationToken cancellationToken)
        {
            if (_offset < _filled)
                return;
            _filled = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            _offset = 0;
            if (_filled == 0)
                throw new InvalidDataException("connection closed inside the chunked request body");
        }
    }
}
