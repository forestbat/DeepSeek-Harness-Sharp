using System.Net;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Dsh.Mcp;

/** 识别"MCP server 重启导致会话失效"的异常: 404 session-not-found、transport 关闭、或非用户中止的 transport 取消。 */
internal static class McpStaleSession
{
    public static bool IsStale(Exception error, CancellationToken signal)
    {
        if (signal.IsCancellationRequested)
            return false;
        for (var current = error; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case HttpRequestException { StatusCode: HttpStatusCode.NotFound }:
                case ClientTransportClosedException { Details: HttpClientCompletionDetails { HttpStatusCode: HttpStatusCode.NotFound } }:
                case OperationCanceledException:
                    return true;
                case HttpRequestException http when IsSessionNotFound(http.Message):
                case McpException mcp when IsSessionNotFound(mcp.Message):
                    return true;
            }
        }
        return false;
    }

    private static bool IsSessionNotFound(string? message)
        => message is not null
            && message.Contains("session", StringComparison.OrdinalIgnoreCase)
            && message.Contains("not found", StringComparison.OrdinalIgnoreCase);
}
