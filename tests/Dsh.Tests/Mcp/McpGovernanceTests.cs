using System.IO.Pipes;
using System.Net;
using System.Text.Json;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Mcp;
using Dsh.Runtime;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dsh.Tests.Mcp;

public sealed class McpGovernanceTests
{
    [Fact]
    public async Task SemanticGovernance_DetectsNameCollisionAndRedundancy()
    {
        using var home = new TemporaryHome();
        var ctx = new Context();
        ctx.SetOwn("dshHomePath", home.Path);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);

        var (client1, server1) = CreateServerPair("serverA", [
            McpServerTool.Create(() => "ast", new McpServerToolCreateOptions { Name = "diagnostics", Description = "Roslyn compiler AST diagnostics" }),
            McpServerTool.Create(() => "same", new McpServerToolCreateOptions { Name = "common_tool", Description = "Common utility tool" }),
        ]);
        await using var s1 = server1;
        _ = server1.RunAsync(TestContext.Current.CancellationToken);

        var (client2, server2) = CreateServerPair("serverB", [
            McpServerTool.Create(() => "symbols", new McpServerToolCreateOptions { Name = "codelens_symbols", Description = "CodeLens syntax and symbol search" }),
            McpServerTool.Create(() => "same", new McpServerToolCreateOptions { Name = "common_tool", Description = "Common utility tool" }),
        ]);
        await using var s2 = server2;
        _ = server2.RunAsync(TestContext.Current.CancellationToken);

        await using var service = new McpService(ctx, home: new HarnessHome(home.Path));
        await service.ConnectAsync(new McpServerConfig("serverA", "stdio"), client1, TestContext.Current.CancellationToken);
        await service.ConnectAsync(new McpServerConfig("serverB", "stdio"), client2, TestContext.Current.CancellationToken);

        var report = service.GovernanceReport;
        Assert.NotNull(report);
        Assert.Contains(report.Conflicts, c => c.Kind == McpConflictKind.NameCollision && c.ToolNames.Contains("common_tool"));
        Assert.Contains(report.Conflicts, c => c.Kind == McpConflictKind.SemanticRedundancy);
        Assert.True(File.Exists(report.TipsPath));
    }

    [Fact]
    public async Task SemanticGovernance_DetectsHostToolShadowing_PrioritizesSupportedIdesOverDefault()
    {
        using var home = new TemporaryHome();
        var ctx = new Context();
        ctx.SetOwn("dshHomePath", home.Path);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);

        var (clientRider, serverRider) = CreateServerPair("rider-tools", [
            McpServerTool.Create(() => "code", new McpServerToolCreateOptions { Name = "read_file", Description = "Rider IDE read file" }),
        ]);
        await using var sRider = serverRider;
        _ = serverRider.RunAsync(TestContext.Current.CancellationToken);

        var (clientVsCode, serverVsCode) = CreateServerPair("vscode-tools", [
            McpServerTool.Create(() => "code", new McpServerToolCreateOptions { Name = "edit_file", Description = "VS Code editor" }),
        ]);
        await using var sVsCode = serverVsCode;
        _ = serverVsCode.RunAsync(TestContext.Current.CancellationToken);

        var (clientGeneric, serverGeneric) = CreateServerPair("generic-server", [
            McpServerTool.Create(() => "data", new McpServerToolCreateOptions { Name = "write_file", Description = "Generic write file" }),
        ]);
        await using var sGeneric = serverGeneric;
        _ = serverGeneric.RunAsync(TestContext.Current.CancellationToken);

        await using var service = new McpService(ctx, home: new HarnessHome(home.Path));
        await service.ConnectAsync(new McpServerConfig("rider-tools", "stdio"), clientRider, TestContext.Current.CancellationToken);
        await service.ConnectAsync(new McpServerConfig("vscode-tools", "stdio"), clientVsCode, TestContext.Current.CancellationToken);
        await service.ConnectAsync(new McpServerConfig("generic-server", "stdio"), clientGeneric, TestContext.Current.CancellationToken);

        var report = service.GovernanceReport;
        Assert.NotNull(report);
        var riderConflict = Assert.Single(report.Conflicts, c => c.ServerNames.Contains("rider-tools"));
        Assert.Equal(McpResolutionAction.PreferServer, riderConflict.SuggestedAction);
        Assert.Equal("rider-tools", riderConflict.TargetServerOrTool);

        var vsCodeConflict = Assert.Single(report.Conflicts, c => c.ServerNames.Contains("vscode-tools"));
        Assert.Equal(McpResolutionAction.PreferServer, vsCodeConflict.SuggestedAction);
        Assert.Equal("vscode-tools", vsCodeConflict.TargetServerOrTool);

        var genericConflict = Assert.Single(report.Conflicts, c => c.ServerNames.Contains("generic-server"));
        Assert.Equal(McpResolutionAction.PrioritizeHostTool, genericConflict.SuggestedAction);

        var tipsContent = await File.ReadAllTextAsync(report.TipsPath, TestContext.Current.CancellationToken);
        Assert.Contains("优先使用 IDE 扩展 MCP 工具代替内置普通工具", tipsContent);
        Assert.DoesNotContain("IDE/Rider", tipsContent);
    }

    [Fact]
    public async Task ResolutionStore_Approve_DisablesConflictingTool_AndUpdatesRegistrations()
    {
        using var home = new TemporaryHome();
        var ctx = new Context();
        ctx.SetOwn("dshHomePath", home.Path);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);

        var (clientA, serverA) = CreateServerPair("srvA", [
            McpServerTool.Create(() => "a", new McpServerToolCreateOptions { Name = "duplicate_tool", Description = "Utility from server A" }),
        ]);
        await using var sA = serverA;
        _ = serverA.RunAsync(TestContext.Current.CancellationToken);

        var (clientB, serverB) = CreateServerPair("srvB", [
            McpServerTool.Create(() => "b", new McpServerToolCreateOptions { Name = "duplicate_tool", Description = "Utility from server B" }),
        ]);
        await using var sB = serverB;
        _ = serverB.RunAsync(TestContext.Current.CancellationToken);

        await using var service = new McpService(ctx, home: new HarnessHome(home.Path));
        await service.ConnectAsync(new McpServerConfig("srvA", "stdio"), clientA, TestContext.Current.CancellationToken);
        await service.ConnectAsync(new McpServerConfig("srvB", "stdio"), clientB, TestContext.Current.CancellationToken);

        Assert.NotNull(tools.Get("mcp_srvA_duplicate_tool"));
        Assert.NotNull(tools.Get("mcp_srvB_duplicate_tool"));

        var conflict = Assert.Single(service.GovernanceReport!.Conflicts, c => c.Kind == McpConflictKind.NameCollision);
        var customResolution = new McpConflictResolution(
            conflict.ConflictId,
            conflict.Kind,
            ["srvB"],
            ["duplicate_tool"],
            "Disable duplicate tool on srvB",
            McpResolutionAction.DisableTool,
            "duplicate_tool",
            McpResolutionStatus.Pending,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await service.Store.SyncAsync([conflict], TestContext.Current.CancellationToken);
        await service.ApproveResolutionAsync(conflict.ConflictId, TestContext.Current.CancellationToken);

        // Disabling tool on srvB should unregister it from ToolRuntime
        var store = new McpResolutionStore(new HarnessHome(home.Path));
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var reloaded = Assert.Single(store.Resolutions, r => r.Id == conflict.ConflictId);
        Assert.Equal(McpResolutionStatus.Approved, reloaded.Status);
    }

    [Fact]
    public async Task McpCommand_HandlesResolutionsAndTipsCommands()
    {
        using var home = new TemporaryHome();
        var ctx = new Context();
        ctx.SetOwn("dshHomePath", home.Path);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        _ = new ToolRuntime(ctx);
        var commands = CommandsService.Register(ctx);

        var (client, server) = CreateServerPair("local", [
            McpServerTool.Create(() => "1", new McpServerToolCreateOptions { Name = "read_file", Description = "Read file" }),
        ]);
        await using var s = server;
        _ = server.RunAsync(TestContext.Current.CancellationToken);

        await using var service = new McpService(ctx, home: new HarnessHome(home.Path));
        await service.ConnectAsync(new McpServerConfig("local", "stdio"), client, TestContext.Current.CancellationToken);

        using var commandRegistration = McpCommand.Register(ctx, service, new HarnessHome(home.Path));
        var agent = new TestAgent(ctx);

        var statusExec = await commands.Execute(agent, "/mcp", TestContext.Current.CancellationToken);
        Assert.NotNull(statusExec);
        var statusResult = Assert.IsType<CommandResult.Success>(statusExec.Result);
        Assert.Contains("local: stdio connected", statusResult.Text);

        var resolutionsExec = await commands.Execute(agent, "/mcp resolutions", TestContext.Current.CancellationToken);
        Assert.NotNull(resolutionsExec);
        var resolutionsResult = Assert.IsType<CommandResult.Success>(resolutionsExec.Result);
        Assert.Contains("MCP 语义治理建议总数", resolutionsResult.Text);

        var tipsExec = await commands.Execute(agent, "/mcp tips", TestContext.Current.CancellationToken);
        Assert.NotNull(tipsExec);
        var tipsResult = Assert.IsType<CommandResult.Success>(tipsExec.Result);
        Assert.Contains("DSH MCP 工具调和指引", tipsResult.Text);

        var conflict = service.GovernanceReport!.Resolutions.First();
        var approveExec = await commands.Execute(agent, $"/mcp resolve {conflict.Id} approve", TestContext.Current.CancellationToken);
        Assert.NotNull(approveExec);
        var approveResult = Assert.IsType<CommandResult.Success>(approveExec.Result);
        Assert.Contains("已批准并采纳建议", approveResult.Text);
    }

    [Fact]
    public void McpStaleSession_DistinguishesModernVsLegacyProtocols()
    {
        var sessionNotFoundHttp = new HttpRequestException("The session was not found (404)");
        var mcpNotFound = new McpException("session not found");
        var connectionClosed = new ClientTransportClosedException(new StdioClientCompletionDetails { ExitCode = 100 });

        // Legacy protocol treats 404 session-not-found as stale session
        Assert.True(McpStaleSession.IsStale(sessionNotFoundHttp, CancellationToken.None, "2025-11-25"));
        Assert.True(McpStaleSession.IsStale(mcpNotFound, CancellationToken.None, "2025-11-25"));

        // Modern protocol treats 404 session-not-found as non-session error (since sessions do not exist)
        Assert.False(McpStaleSession.IsStale(sessionNotFoundHttp, CancellationToken.None, "2026-07-28"));
        Assert.False(McpStaleSession.IsStale(mcpNotFound, CancellationToken.None, "2026-07-28"));

        // Both treat transport disconnect as connection recovery
        Assert.True(McpStaleSession.IsStale(connectionClosed, CancellationToken.None, "2025-11-25"));
        Assert.True(McpStaleSession.IsStale(connectionClosed, CancellationToken.None, "2026-07-28"));
    }

    [Fact]
    public async Task SafetyBoundary_EnforcesBlacklistAndWhitelist_OnMcpTools()
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        var settings = new HarnessSettings
        {
            Safety = new SafetySettings
            {
                AutoApprove = false,
                Whitelist = new Dictionary<string, PermissionMatcher>
                {
                    ["allow_safe"] = new() { Tool = ["mcp_srv_safe_tool"] },
                },
                Blacklist = new Dictionary<string, PermissionMatcher>
                {
                    ["block_bad"] = new() { Tool = ["mcp_srv_dangerous_tool"] },
                },
            },
        };
        _ = new SafetyPolicySource(ctx, () => settings);

        var (client, server) = CreateServerPair("srv", [
            McpServerTool.Create(() => "safe", new McpServerToolCreateOptions { Name = "safe_tool", Description = "Safe tool" }),
            McpServerTool.Create(() => "bad", new McpServerToolCreateOptions { Name = "dangerous_tool", Description = "Dangerous tool" }),
        ]);
        await using var s = server;
        _ = server.RunAsync(TestContext.Current.CancellationToken);

        await using var service = new McpService(ctx);
        await service.ConnectAsync(new McpServerConfig("srv", "stdio"), client, TestContext.Current.CancellationToken);

        var safeTool = tools.Get("mcp_srv_safe_tool");
        var badTool = tools.Get("mcp_srv_dangerous_tool");
        Assert.NotNull(safeTool);
        Assert.NotNull(badTool);

        // Bad tool must be denied by blacklist
        var badArgs = JsonDocument.Parse("{}").RootElement;
        var badResult = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("call_bad"),
            Name = "mcp_srv_dangerous_tool",
            Arguments = badArgs,
            Signal = TestContext.Current.CancellationToken,
        });
        Assert.True(badResult.IsError);
        Assert.Contains("Blocked by safety.blacklist rule", string.Concat(badResult.Content.OfType<TextBlock>().Select(t => t.Text)));

        // Safe tool matches whitelist and executes without requiring prompt
        var safeArgs = JsonDocument.Parse("{}").RootElement;
        var safeResult = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("call_safe"),
            Name = "mcp_srv_safe_tool",
            Arguments = safeArgs,
            Signal = TestContext.Current.CancellationToken,
        });
        Assert.False(safeResult.IsError);
    }

    private static (IClientTransport Client, McpServer Server) CreateServerPair(
        string serverName,
        IEnumerable<McpServerTool> serverTools)
    {
        var serverOut = new AnonymousPipeServerStream(PipeDirection.Out);
        var serverIn = new AnonymousPipeServerStream(PipeDirection.In);
        var clientOut = new AnonymousPipeClientStream(PipeDirection.Out, serverIn.ClientSafePipeHandle);
        var clientIn = new AnonymousPipeClientStream(PipeDirection.In, serverOut.ClientSafePipeHandle);

        var server = McpServer.Create(
            new StreamServerTransport(serverIn, serverOut, serverName),
            new McpServerOptions
            {
                ServerInfo = new Implementation { Name = serverName, Version = "1.0" },
                ToolCollection = [.. serverTools],
            });

        return (new StreamClientTransport(clientOut, clientIn), server);
    }

    private sealed class TemporaryHome : IDisposable
    {
        public string Path { get; } = TempTree.CreateDirectory("mcp-governance");

        public void Dispose() => TempTree.Delete(Path);
    }

    private sealed class TestAgent(Context ctx) : IAgent
    {
        public SessionId Id => Session.Id;
        public Session Session { get; } = Session.Create(SessionId.Create("s1"), null, new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = SessionId.Create("s1"),
            CreatedAt = 0,
            Cwd = Path.GetTempPath(),
            IsSeeded = false,
        });
        public ScopeKey ScopeKey { get; } = new();
        public Context Ctx => ctx;
        public AgentStatus Status => AgentStatus.Idle;
        public AgentOptions Options { get; } = new();
        public void Cancel(AgentCancelCause cause, bool keepInbox = false) { }
        public Task WhenIdle() => Task.CompletedTask;
        public void Send(UserMessage message, string target, bool wakeup) { }
        public void Followup(UserMessage message) { }
        public void Steer(UserMessage message) { }
        public void Inject(UserMessage message) { }
    }
}
