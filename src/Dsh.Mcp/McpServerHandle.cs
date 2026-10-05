using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Dsh.Mcp;

/**
 * 一个 MCP server 的可换句柄: 持有当前 McpClient 与工具表, 并可按需重连(重新握手取新 session)。
 * 注册到 ToolRuntime 的 ToolDefinition 绑定本句柄而非某个具体 McpClientTool, 因此重连后无需重注册、agent 视角不失效。
 */
internal sealed class McpServerHandle(McpServerConfig config, Func<IClientTransport> transportFactory)
{
    private readonly SemaphoreSlim _reconnectGate = new(1, 1);
    private Live? _live;
    private long _version;
    private string? _error;

    public McpServerConfig Config { get; } = config;

    public long Version => Volatile.Read(ref _version);

    public bool Connected => Volatile.Read(ref _live) is not null;

    public int ToolCount => Volatile.Read(ref _live)?.Tools.Count ?? 0;

    public McpServerStatus Status => new(Config.Name, Config.Transport, Connected, ToolCount, _error);

    public async Task<IList<McpClientTool>> ConnectAsync(CancellationToken signal)
    {
        var client = await McpClient.CreateAsync(transportFactory(), new McpClientOptions(), NullLoggerFactory.Instance, signal);
        var tools = await client.ListToolsAsync(cancellationToken: signal);
        Volatile.Write(ref _live, new Live(client, tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal)));
        _error = null;
        Interlocked.Increment(ref _version);
        return tools;
    }

    public void MarkError(string message) => _error = message;

    public bool TryGetTool(string toolName, out McpClientTool? tool, out long version)
    {
        var live = Volatile.Read(ref _live);
        version = Volatile.Read(ref _version);
        tool = null;
        return live is not null && live.Tools.TryGetValue(toolName, out tool);
    }

    // 只在"调用时用的版本仍是最新"时才重连, 使并发调用共享一次重连。
    public async Task ReconnectIfVersionAsync(long usedVersion, CancellationToken signal)
    {
        if (Volatile.Read(ref _version) != usedVersion)
            return;
        await _reconnectGate.WaitAsync(signal);
        try
        {
            if (Volatile.Read(ref _version) != usedVersion)
                return;
            var stale = Volatile.Read(ref _live)?.Client;
            Volatile.Write(ref _live, null);
            if (stale is not null)
            {
                try
                {
                    await stale.DisposeAsync();
                }
                catch
                {
                    // 已重启的 server 可能拒绝旧 session 的 DELETE。
                }
            }
            try
            {
                await ConnectAsync(signal);
            }
            catch (Exception error)
            {
                _error = error.Message;
                throw;
            }
        }
        finally
        {
            _reconnectGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var client = Volatile.Read(ref _live)?.Client;
        Volatile.Write(ref _live, null);
        if (client is not null)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch
            {
                // 尽最大努力释放。
            }
        }
        _reconnectGate.Dispose();
    }

    private sealed record Live(McpClient Client, IReadOnlyDictionary<string, McpClientTool> Tools);
}
