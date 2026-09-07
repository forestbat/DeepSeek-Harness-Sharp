using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Dsh.Mcp;

public sealed record McpServerConfig(
    string Name,
    string Transport,
    string? Command = null,
    IReadOnlyList<string>? Arguments = null,
    string? Url = null);

public sealed record McpServerStatus(string Name, string Transport, bool Connected, int ToolCount, string? Error = null);

public sealed class McpRuntime : IAsyncDisposable
{
    private readonly List<McpConnection> _connections = [];

    public IReadOnlyList<McpServerStatus> Status()
        => _connections.Select(connection => connection.Status).ToList();

    public async Task ConnectAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken cancellationToken = default)
    {
        foreach (var server in servers)
        {
            try
            {
                var transport = CreateTransport(server);
                var client = await McpClient.CreateAsync(transport, new McpClientOptions(), NullLoggerFactory.Instance, cancellationToken);
                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
                _connections.Add(new McpConnection(server, client, transport, tools.Count));
            }
            catch (Exception error)
            {
                _connections.Add(new McpConnection(server, null, null, 0, error.Message));
            }
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

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            if (connection.Client is not null)
                await connection.Client.DisposeAsync();
        }
        _connections.Clear();
    }

    private sealed class McpConnection(McpServerConfig Config, McpClient? Client, IClientTransport? Transport, int ToolCount, string? Error = null)
    {
        public McpClient? Client { get; } = Client;
        public IClientTransport? Transport { get; } = Transport;
        public McpServerStatus Status { get; } = new(Config.Name, Config.Transport, Client is not null, ToolCount, Error);
    }
}