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
 * 常驻 MCP 服务: 进程存活期内保持与各 server 的连接, 并把每个 MCP 工具注册为 agent 原生工具。
 * 负责变更时的语义治理、调和提示词生成与冲突消解持久化。
 */
public sealed class McpService(
    Context ctx,
    int disclosureThreshold = ToolsSettings.DefaultDisclosureThreshold,
    string? workingDirectory = null,
    HarnessHome? home = null) : Service(ctx, ServiceName), IAsyncDisposable
{
    public const string ServiceName = "mcp";

    private readonly HarnessHome? _home = home ?? (ctx.GetProp("dshHomePath") is string root ? new HarnessHome(root) : null);
    private readonly McpResolutionStore _store = new(home ?? (ctx.GetProp("dshHomePath") is string root2 ? new HarnessHome(root2) : null));
    private readonly List<Connection> _connections = [];
    private readonly Lock _gate = new();
    private IReadOnlyList<IDisposable> _resourceTools = [];
    private McpGovernanceReport? _governanceReport;

    public McpResolutionStore Store => _store;

    public McpGovernanceReport? GovernanceReport
    {
        get
        {
            lock (_gate)
                return _governanceReport;
        }
    }

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
        await _store.InitializeAsync(signal);
        foreach (var server in servers)
            await ConnectOneAsync(server, () => CreateTransport(server, signal), signal);
        await RunGovernanceAsync(signal);
    }

    internal async Task ConnectAsync(McpServerConfig server, IClientTransport transport, CancellationToken signal = default)
    {
        await _store.InitializeAsync(signal);
        await ConnectOneAsync(server, () => transport, signal);
        await RunGovernanceAsync(signal);
    }

    internal async Task ConnectAsync(McpServerConfig server, Func<IClientTransport> transportFactory, CancellationToken signal = default)
    {
        await _store.InitializeAsync(signal);
        await ConnectOneAsync(server, transportFactory, signal);
        await RunGovernanceAsync(signal);
    }

    public async Task ReloadAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken signal = default)
    {
        await DisconnectAllAsync();
        await ConnectAsync(servers, signal);
    }

    public async Task<McpGovernanceReport> RunGovernanceAsync(CancellationToken signal = default)
    {
        await _store.InitializeAsync(signal);
        var handles = Handles();
        var conflicts = McpSemanticAnalyzer.Analyze(handles);
        var resolutions = await _store.SyncAsync(conflicts, signal);
        var tipsResult = await McpTipsGenerator.GenerateAndSaveAsync(Ctx, _home, handles, resolutions, signal);
        var tipsPath = tipsResult.TipsPath ?? (_home is not null ? Path.Combine(_home.Root, McpTipsGenerator.DefaultFileName) : McpTipsGenerator.DefaultFileName);
        var report = new McpGovernanceReport(
            conflicts,
            resolutions,
            tipsPath,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            tipsResult.SkillPaths);
        lock (_gate)
            _governanceReport = report;
        return report;
    }

    public async Task<bool> ApproveResolutionAsync(string id, CancellationToken signal = default)
    {
        var ok = await _store.ApproveAsync(id, signal);
        if (ok)
        {
            RefreshToolRegistrations();
            await RunGovernanceAsync(signal);
        }
        return ok;
    }

    public async Task<bool> RejectResolutionAsync(string id, CancellationToken signal = default)
    {
        var ok = await _store.RejectAsync(id, signal);
        if (ok)
        {
            RefreshToolRegistrations();
            await RunGovernanceAsync(signal);
        }
        return ok;
    }

    public async ValueTask DisposeAsync() => await DisconnectAllAsync();

    private async Task ConnectOneAsync(McpServerConfig server, Func<IClientTransport> transportFactory, CancellationToken signal)
    {
        var handle = new McpServerHandle(server, transportFactory);
        try
        {
            var tools = await handle.ConnectAsync(signal);
            Track(new Connection(handle, RegisterTools(handle, tools), tools));
        }
        catch (Exception error)
        {
            handle.MarkError(error.Message);
            Track(new Connection(handle, [], []));
        }
    }

    private IReadOnlyList<IDisposable> RegisterTools(McpServerHandle handle, IList<McpClientTool> tools)
    {
        var runtime = Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var activeTools = tools.Where(tool => !_store.IsToolDisabled(handle.Config.Name, tool.Name)).ToList();
        var grouped = Ctx.GetProp(ToolDisclosureSwitch.PropName) is true && activeTools.Count >= disclosureThreshold;
        var routerSummary = McpTipsGenerator.GenerateRouterPrompt(handle);
        var group = grouped ? new ToolDisclosure(
            $"mcp:{handle.Config.Name}",
            routerSummary) : null;
        return activeTools
            .Select(tool => runtime.Register(McpToolBridge.Wrap(handle, tool, group)))
            .ToList();
    }

    private void RefreshToolRegistrations()
    {
        lock (_gate)
        {
            for (var i = 0; i < _connections.Count; i++)
            {
                var conn = _connections[i];
                foreach (var reg in conn.Registrations)
                    reg.Dispose();
                var newRegs = RegisterTools(conn.Handle, conn.Tools);
                _connections[i] = conn with { Registrations = newRegs };
            }
        }
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

    private IClientTransport CreateTransport(McpServerConfig server, CancellationToken signal)
    {
        if (server.Transport == "stdio")
        {
            if (string.IsNullOrWhiteSpace(server.Command))
                throw new ArgumentException($"mcp server \"{server.Name}\" requires a command for stdio transport");
            var home = _home ?? (Ctx.GetProp("dshHomePath") is string root ? new HarnessHome(root) : null);
            if (home is null || HarnessSettings.LoadForReview(home).Sandbox?.Enabled != true)
                return new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = server.Name,
                    Command = server.Command,
                    Arguments = [.. server.Arguments ?? []],
                });
            var subprocess = Ctx.Get<SubprocessService>(SubprocessService.ServiceName, false)
                ?? throw new InvalidOperationException("Owned MCP stdio requires the subprocess service.");
            SafetyBoundary.Resolve(Ctx)?.AuthorizeConfiguredLifecycle(SafetyBoundary.SafetyNamespace, server.Name, signal);
            return new OwnedStdioClientTransport(subprocess, server, workingDirectory ?? Environment.CurrentDirectory, home.SettingsFile);
        }

        if (server.Transport is "streamable-http" or "sse")
        {
            if (string.IsNullOrWhiteSpace(server.Url))
                throw new ArgumentException($"mcp server \"{server.Name}\" requires a url for {server.Transport} transport");
            return new HttpClientTransport(
                new HttpClientTransportOptions { Name = server.Name, Endpoint = new Uri(server.Url) },
                new HttpClient(OutboundProxy.FromContext(Ctx).CreateHandler()), ownsHttpClient: true);
        }

        throw new ArgumentException($"mcp server \"{server.Name}\" has unsupported transport \"{server.Transport}\"");
    }

    private sealed record Connection(
        McpServerHandle Handle,
        IReadOnlyList<IDisposable> Registrations,
        IList<McpClientTool> Tools);
}
