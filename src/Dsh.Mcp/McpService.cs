using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;
using ModelContextProtocol.Client;

namespace Dsh.Mcp;

public sealed record McpServerConfig(
    string Name,
    string Transport,
    string? Command = null,
    IReadOnlyList<string>? Arguments = null,
    string? Url = null)
{
    public static List<McpServerConfig> FromSettings(HarnessSettings settings)
        => settings.McpServers
            .Where(entry => entry.Value.Enabled)
            .Select(entry => FromSettingsEntry(entry.Key, entry.Value))
            .ToList();

    private static McpServerConfig FromSettingsEntry(string name, McpServerSettings settings)
    {
        var commandParts = settings.Command ?? [];
        var command = commandParts.Count > 0 ? commandParts[0] : null;
        var arguments = commandParts.Skip(1).Concat(settings.Args ?? []).ToList();
        return new McpServerConfig(name, settings.Transport ?? "stdio", command, arguments, settings.Url);
    }
}

public sealed record McpServerStatus(
    string Name,
    string Transport,
    bool Connected,
    int ToolCount,
    string? Error = null,
    bool Resources = false,
    long ResourceListChanges = 0);

/**
 * 常驻 MCP 服务:进程存活期内保持与各 server 的连接,并把每个 MCP 工具注册为 agent 原生工具。
 * 至少有一个 server 在册时, 另注册按 server 名取资源的三个共享资源工具。
 */
public sealed class McpService(Context ctx, int disclosureThreshold = ToolsSettings.DefaultDisclosureThreshold)
    : Service(ctx, ServiceName), IAsyncDisposable
{
    public const string ServiceName = "mcp";

    private readonly List<Connection> _connections = [];
    private readonly Lock _gate = new();
    private IReadOnlyList<IDisposable> _resourceTools = [];

    public IReadOnlyList<McpServerStatus> Status()
    {
        lock (_gate)
            return _connections.Select(connection => connection.Handle.Status).ToList();
    }

    internal IReadOnlyList<McpServerHandle> Handles()
    {
        lock (_gate)
            return _connections.Select(connection => connection.Handle).ToList();
    }

    public async Task ConnectAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken signal = default)
    {
        foreach (var server in servers)
            await ConnectOneAsync(server, () => CreateTransport(server, OutboundProxy.FromContext(Ctx)), signal);
    }

    internal async Task ConnectAsync(McpServerConfig server, IClientTransport transport, CancellationToken signal = default)
        => await ConnectOneAsync(server, () => transport, signal);

    internal async Task ConnectAsync(McpServerConfig server, Func<IClientTransport> transportFactory, CancellationToken signal = default)
        => await ConnectOneAsync(server, transportFactory, signal);

    public async Task ReloadAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken signal = default)
    {
        await DisconnectAllAsync();
        await ConnectAsync(servers, signal);
    }

    public async ValueTask DisposeAsync() => await DisconnectAllAsync();

    private async Task ConnectOneAsync(McpServerConfig server, Func<IClientTransport> transportFactory, CancellationToken signal)
    {
        var handle = new McpServerHandle(server, transportFactory);
        try
        {
            var tools = await handle.ConnectAsync(signal);
            Track(new Connection(handle, RegisterTools(handle, tools)));
        }
        catch (Exception error)
        {
            handle.MarkError(error.Message);
            Track(new Connection(handle, []));
        }
    }

    private IReadOnlyList<IDisposable> RegisterTools(McpServerHandle handle, IList<McpClientTool> tools)
    {
        var runtime = Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        // 只有工具够多、且用户打开了渐进式披露, 才值得走路由展开: 小服务器直接可见, 省掉一次额外往返。
        var grouped = Ctx.GetProp(ToolDisclosureSwitch.PropName) is true && tools.Count >= disclosureThreshold;
        var group = grouped ? new ToolDisclosure(
            $"mcp:{handle.Config.Name}",
            $"tools exposed by the MCP server \"{handle.Config.Name}\"") : null;
        return tools
            .Select(tool => runtime.Register(McpToolBridge.Wrap(handle, tool, group)))
            .ToList();
    }

    private void Track(Connection connection)
    {
        bool first;
        lock (_gate)
        {
            first = _connections.Count == 0;
            _connections.Add(connection);
        }
        if (first)
            _resourceTools = McpResourceTools.Register(Ctx, this);
    }

    private async Task DisconnectAllAsync()
    {
        List<Connection> connections;
        lock (_gate)
        {
            connections = [.. _connections];
            _connections.Clear();
        }
        foreach (var registration in Interlocked.Exchange(ref _resourceTools, []))
            registration.Dispose();
        foreach (var connection in connections)
        {
            foreach (var registration in connection.Registrations)
                registration.Dispose();
            await connection.Handle.DisposeAsync();
        }
    }

    private static IClientTransport CreateTransport(McpServerConfig server, OutboundProxy proxy)
    {
        if (server.Transport == "stdio")
        {
            if (string.IsNullOrWhiteSpace(server.Command))
                throw new ArgumentException($"mcp server \"{server.Name}\" requires a command for stdio transport");
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.Command,
                Arguments = [.. server.Arguments ?? []],
            });
        }

        if (server.Transport is "streamable-http" or "sse")
        {
            if (string.IsNullOrWhiteSpace(server.Url))
                throw new ArgumentException($"mcp server \"{server.Name}\" requires a url for {server.Transport} transport");
            return new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = server.Name,
                    Endpoint = new Uri(server.Url),
                },
                new HttpClient(proxy.CreateHandler()),
                ownsHttpClient: true);
        }

        throw new ArgumentException($"mcp server \"{server.Name}\" has unsupported transport \"{server.Transport}\"");
    }

    private sealed record Connection(McpServerHandle Handle, IReadOnlyList<IDisposable> Registrations);
}
