using System.Net;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Dsh.Mcp;

/** 识别"MCP server 会话失效或连接断开"异常: 区分 Modern 无状态连接恢复与 Legacy 会话失效。 */
internal static class McpStaleSession
{
    public const string ModernProtocolVersion = "2026-07-28";

    public static bool IsModern(string? version)
        => !string.IsNullOrEmpty(version) && string.CompareOrdinal(version, ModernProtocolVersion) >= 0;

    public static bool IsStale(Exception error, CancellationToken signal, string? negotiatedVersion = null)
    {
        if (signal.IsCancellationRequested)
            return false;
        var modern = IsModern(negotiatedVersion);
        for (var current = error; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case HttpRequestException { StatusCode: HttpStatusCode.NotFound } when !modern:
                case McpNotificationStreamEndedException:
                case ClientTransportClosedException { Details: HttpClientCompletionDetails { HttpStatusCode: HttpStatusCode.NotFound } } when !modern:
                case ClientTransportClosedException:
                case OperationCanceledException:
                    return true;
                case HttpRequestException http when !modern && IsSessionNotFound(http.Message):
                case McpException mcp when !modern && IsSessionNotFound(mcp.Message):
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
