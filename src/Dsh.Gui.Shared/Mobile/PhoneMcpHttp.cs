using System.Text;

namespace Dsh.Gui.Shared.Mobile;

/**
 * 手机上的一问一答 HTTP 服务端: 只为 MCP 的 streamable-http 客户端服务。
 * 每个连接只处理一个请求就关闭, 因此不需要 keep-alive 状态机, 也不会把连接对象留在字段里。
 * 令牌既可以放在路径(/mcp/{token}), 也可以放在 X-Dsh-Token 或 Authorization: Bearer。
 */
public sealed class PhoneMcpHttp(PhoneMcpProtocol protocol, string token)
{
    public const string EndpointPath = "/mcp";
    public const string HealthPath = "/health";

    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 1024 * 1024;

    public async Task ServeOnceAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var head = await ReadHeadAsync(input, cancellationToken).ConfigureAwait(false);
        if (head is null)
            return;
        var (requestLine, headers, remainder) = head.Value;
        var parts = requestLine.Split(' ');
        if (parts.Length < 2)
        {
            await WriteAsync(output, 400, "text/plain", "bad request", cancellationToken).ConfigureAwait(false);
            return;
        }
        var method = parts[0].ToUpperInvariant();
        var target = parts[1];
        var path = target.Split('?')[0];
        if (method == "GET" && path == HealthPath)
        {
            await WriteAsync(output, 200, "text/plain; charset=utf-8", "ok", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (path != EndpointPath && !path.StartsWith($"{EndpointPath}/", StringComparison.Ordinal))
        {
            await WriteAsync(output, 404, "text/plain", "not found", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!TokenMatches(path, headers))
        {
            await WriteAsync(output, 401, "text/plain", "unauthorized", cancellationToken).ConfigureAwait(false);
            return;
        }
        // 客户端断开会发 DELETE; 本服务无会话状态, 直接接受。
        if (method == "DELETE")
        {
            await WriteAsync(output, 202, "application/json", "", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (method != "POST")
        {
            await WriteAsync(output, 405, "text/plain", "method not allowed", cancellationToken).ConfigureAwait(false);
            return;
        }
        var body = await ReadBodyAsync(input, headers, remainder, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            await WriteAsync(output, 413, "text/plain", "request too large", cancellationToken).ConfigureAwait(false);
            return;
        }
        var reply = await protocol.HandleAsync(body, cancellationToken).ConfigureAwait(false);
        if (reply is null)
        {
            await WriteAsync(output, 202, "application/json", "", cancellationToken).ConfigureAwait(false);
            return;
        }
        await WriteAsync(output, 200, "application/json", reply, cancellationToken).ConfigureAwait(false);
    }

    private bool TokenMatches(string path, IReadOnlyDictionary<string, string> headers)
    {
        if (token.Length == 0)
            return true;
        var suffix = path.Length > EndpointPath.Length ? path[(EndpointPath.Length + 1)..] : "";
        if (string.Equals(suffix, token, StringComparison.Ordinal))
            return true;
        if (headers.TryGetValue("x-dsh-token", out var header) && string.Equals(header, token, StringComparison.Ordinal))
            return true;
        return headers.TryGetValue("authorization", out var authorization)
            && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && string.Equals(authorization["Bearer ".Length..].Trim(), token, StringComparison.Ordinal);
    }

    private static async Task<(string RequestLine, Dictionary<string, string> Headers, byte[] Remainder)?> ReadHeadAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        while (buffer.Length < MaxHeaderBytes)
        {
            var read = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return null;
            buffer.Write(chunk, 0, read);
            var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0)
                continue;
            var lines = text[..end].Split("\r\n");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            var consumed = Encoding.UTF8.GetByteCount(text[..(end + 4)]);
            var remainder = buffer.ToArray()[consumed..];
            return (lines[0], headers, remainder);
        }
        return null;
    }

    private static async Task<string?> ReadBodyAsync(
        Stream input,
        IReadOnlyDictionary<string, string> headers,
        byte[] remainder,
        CancellationToken cancellationToken)
    {
        var expected = headers.TryGetValue("content-length", out var text) && int.TryParse(text, out var length)
            ? length
            : 0;
        if (expected > MaxBodyBytes)
            return null;
        var body = new byte[expected];
        var copied = Math.Min(remainder.Length, expected);
        Array.Copy(remainder, body, copied);
        while (copied < expected)
        {
            var read = await input.ReadAsync(body.AsMemory(copied, expected - copied), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            copied += read;
        }
        return Encoding.UTF8.GetString(body, 0, copied);
    }

    private static async Task WriteAsync(Stream output, int status, string contentType, string body, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var reason = status switch
        {
            200 => "OK",
            202 => "Accepted",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            405 => "Method Not Allowed",
            413 => "Payload Too Large",
            _ => "Error",
        };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
        await output.WriteAsync(Encoding.UTF8.GetBytes(head), cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0)
            await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
