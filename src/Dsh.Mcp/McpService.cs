using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
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

public sealed record McpServerStatus(string Name, string Transport, bool Connected, int ToolCount, string? Error = null);

/** 常驻 MCP 服务:进程存活期内保持与各 server 的连接,并把每个 MCP 工具注册为 agent 原生工具。 */
public sealed class McpService(Context ctx) : Service(ctx, ServiceName), IAsyncDisposable
{
    public const string ServiceName = "mcp";

    private readonly List<McpConnection> _connections = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<McpServerStatus> Status()
    {
        lock (_gate)
            return _connections.Select(connection => connection.Status).ToList();
    }

    public async Task ConnectAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken signal = default)
    {
        foreach (var server in servers)
        {
            try
            {
                await ConnectOneAsync(server, CreateTransport(server), signal);
            }
            catch (Exception error)
            {
                Track(new McpConnection(server, null, [], error.Message));
            }
        }
    }

    internal async Task ConnectAsync(McpServerConfig server, IClientTransport transport, CancellationToken signal = default)
        => await ConnectOneAsync(server, transport, signal);

    public async Task ReloadAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken signal = default)
    {
        await DisconnectAllAsync();
        await ConnectAsync(servers, signal);
    }

    public async ValueTask DisposeAsync() => await DisconnectAllAsync();

    private async Task ConnectOneAsync(McpServerConfig server, IClientTransport transport, CancellationToken signal)
    {
        try
        {
            var client = await McpClient.CreateAsync(transport, new McpClientOptions(), NullLoggerFactory.Instance, signal);
            var tools = await client.ListToolsAsync(cancellationToken: signal);
            var registrations = RegisterTools(server.Name, tools);
            Track(new McpConnection(server, client, registrations));
        }
        catch (Exception error)
        {
            Track(new McpConnection(server, null, [], error.Message));
        }
    }

    private IReadOnlyList<IDisposable> RegisterTools(string serverName, IList<McpClientTool> tools)
    {
        var runtime = Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        return tools
            .Select(tool => runtime.Register(McpToolBridge.Wrap(serverName, tool)))
            .ToList();
    }

    private void Track(McpConnection connection)
    {
        lock (_gate)
            _connections.Add(connection);
    }

    private async Task DisconnectAllAsync()
    {
        List<McpConnection> connections;
        lock (_gate)
        {
            connections = [.. _connections];
            _connections.Clear();
        }
        foreach (var connection in connections)
        {
            foreach (var registration in connection.Registrations)
                registration.Dispose();
            if (connection.Client is not null)
                await connection.Client.DisposeAsync();
        }
    }

    private static IClientTransport CreateTransport(McpServerConfig server)
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
            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.Name,
                Endpoint = new Uri(server.Url),
            });
        }

        throw new ArgumentException($"mcp server \"{server.Name}\" has unsupported transport \"{server.Transport}\"");
    }

    private sealed class McpConnection(McpServerConfig config, McpClient? client, IReadOnlyList<IDisposable> registrations, string? error = null)
    {
        public McpClient? Client { get; } = client;
        public IReadOnlyList<IDisposable> Registrations { get; } = registrations;
        public McpServerStatus Status { get; } = new(config.Name, config.Transport, client is not null, registrations.Count, error);
    }
}
