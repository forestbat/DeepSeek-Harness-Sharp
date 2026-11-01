using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Dsh.Tests.Mcp;

[Collection(SerialProcessCollection.Name)]
public sealed class McpSettingsBoundaryTests
{
    [Fact(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    public async Task OwnedStdio_PreservesToolOutputOrRefusesUnsupportedWritableCwd_ButNeverMutatesSettings()
    {
        using var fixture = ExternalProcessBoundaryTests.ProtectedFixture();
        var tools = Tools(fixture);
        await using var service = new McpService(fixture.Context, fixture.Root);
        var output = Path.Combine(fixture.Root, "mcp-output");
        await Connect(service, expectWritableCwdRefusal: OperatingSystem.IsWindows());
        if (OperatingSystem.IsWindows())
        {
            Assert.False(File.Exists(output));
            Assert.Equal(fixture.InitialSettings, await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
            return;
        }
        var permitted = await Execute(tools, "write_file", new { path = output, content = "workspace 中文" }, TestContext.Current.CancellationToken);
        Assert.False(permitted.IsError, Text(permitted));
        Assert.Contains("workspace 中文", Text(permitted));
        Assert.Equal("workspace 中文", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        var denied = await Execute(tools, "write_file", new { path = fixture.SettingsPath, content = "bad" }, TestContext.Current.CancellationToken);
        Assert.True(denied.IsError);
        Assert.Equal(fixture.InitialSettings, await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    public async Task OwnedStdio_CrashDoesNotReplay_OrRefusesUnsupportedWritableCwdBeforeMutation()
    {
        using var fixture = ExternalProcessBoundaryTests.ProtectedFixture();
        var tools = Tools(fixture);
        await using var service = new McpService(fixture.Context, fixture.Root);
        var ledger = Path.Combine(fixture.Root, "mcp-once");
        await Connect(service, expectWritableCwdRefusal: OperatingSystem.IsWindows());
        if (OperatingSystem.IsWindows())
        {
            Assert.False(File.Exists(ledger));
            Assert.Equal(fixture.InitialSettings, await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
            return;
        }
        var result = await Execute(tools, "mutate_then_exit", new { path = ledger }, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Equal("once\n", await File.ReadAllTextAsync(ledger, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 60000)]
    [Trait("Category", "SandboxNative")]
    public async Task OwnedStdio_CancellationDoesNotRestart_OrRefusesUnsupportedWritableCwdBeforeStarting()
    {
        using var fixture = ExternalProcessBoundaryTests.ProtectedFixture();
        var tools = Tools(fixture);
        await using var service = new McpService(fixture.Context, fixture.Root);
        var marker = Path.Combine(fixture.Root, "mcp-ready");
        await Connect(service, expectWritableCwdRefusal: OperatingSystem.IsWindows());
        if (OperatingSystem.IsWindows())
        {
            Assert.False(File.Exists(marker));
            Assert.Equal(fixture.InitialSettings, await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
            return;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = Execute(tools, "wait_file", new { path = marker }, cancellation.Token);
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        readiness.CancelAfter(TimeSpan.FromSeconds(20));
        while (!File.Exists(marker))
        {
            if (running.IsCompleted)
                Assert.Fail("The owned MCP server did not enter its real waiting operation.");
            await Task.Delay(10, readiness.Token);
        }
        cancellation.Cancel();
        var cancelled = await running.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.True(cancelled.IsError);
        var after = Path.Combine(fixture.Root, "mcp-after-cancel");
        var subsequent = await Execute(tools, "write_file", new { path = after, content = "must not restart" }, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.True(subsequent.IsError);
        Assert.False(File.Exists(after));
    }

    [Fact(Timeout = 5000)]
    public async Task RemoteTransport_IsExplicitlyUnavailableInsteadOfClaimingOsIsolation()
    {
        using var fixture = new ExternalProcessBoundaryTests.Fixture(sandboxEnabled: true);
        _ = Tools(fixture);
        await using var service = new McpService(fixture.Context, fixture.Root);
        await service.ConnectAsync([new McpServerConfig("remote", "streamable-http", Url: "https://example.invalid/mcp")], TestContext.Current.CancellationToken);
        var status = Assert.Single(service.Status());
        Assert.False(status.Connected);
        Assert.Equal(fixture.InitialSettings, await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
        Assert.Equal(0, status.ToolCount);
    }

    [Fact(Timeout = 60000)]
    public async Task PublicUnownedBridge_CannotDispatchModelWritesToAnOperatorStartedServer()
    {
        using var fixture = new ExternalProcessBoundaryTests.Fixture(sandboxEnabled: true);
        var tools = Tools(fixture);
        var config = McpResourceServerFixture.ServerConfig("operator", false, "--file-tools");
        // This operator-owned connection is intentionally outside the model's OS execution domain.
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = config.Name,
            Command = config.Command!,
            Arguments = [.. config.Arguments ?? []],
        }), new McpClientOptions(), NullLoggerFactory.Instance, TestContext.Current.CancellationToken);
        var write = Assert.Single(await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken),
            tool => tool.Name == "write_file");
        using var registration = tools.Register(McpToolBridge.Wrap("owned", write));
        var output = Path.Combine(fixture.Root, "unowned-write");
        var denied = await Execute(tools, "write_file", new { path = output, content = "must not dispatch" }, TestContext.Current.CancellationToken);
        Assert.True(denied.IsError);
        Assert.False(File.Exists(output));
    }

    [Fact(Timeout = 60000)]
    public async Task DefaultOff_StdioToolRetainsDirectSettingsIo()
    {
        using var fixture = new ExternalProcessBoundaryTests.Fixture();
        var tools = Tools(fixture);
        await using var service = new McpService(fixture.Context, fixture.Root);
        await Connect(service);
        var result = await Execute(tools, "write_file", new { path = fixture.SettingsPath, content = "stdio 中文" }, TestContext.Current.CancellationToken);
        Assert.False(result.IsError, Text(result));
        Assert.Equal("stdio 中文", await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 60000)]
    public async Task DefaultOff_RemoteHttpToolRetainsDirectSettingsIo()
    {
        using var fixture = new ExternalProcessBoundaryTests.Fixture();
        var tools = Tools(fixture);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ContentRootPath = fixture.Root,
            Args = [],
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer().WithHttpTransport().WithTools(new[]
        {
            McpServerTool.Create((string path, string content) =>
            {
                File.WriteAllText(path, content);
                return File.ReadAllText(path);
            }, new McpServerToolCreateOptions { Name = "write_file" }),
        });
        await using var server = builder.Build();
        server.MapMcp("/mcp");
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var service = new McpService(fixture.Context, fixture.Root);
            await service.ConnectAsync([new McpServerConfig("owned", "streamable-http",
                Url: server.Urls.Single() + "/mcp")], TestContext.Current.CancellationToken);
            var status = Assert.Single(service.Status());
            Assert.True(status.Connected, status.Error);
            var result = await Execute(tools, "write_file", new { path = fixture.SettingsPath, content = "http 中文" }, TestContext.Current.CancellationToken);
            Assert.False(result.IsError, Text(result));
            Assert.Equal("http 中文", await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact(Timeout = 10000)]
    [Trait("Category", "SandboxNative")]
    public async Task MacEnabled_OwnedStdioRefusesUnsupportedChildExecution()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Skip("The unsupported arbitrary-child ownership combination is macOS-specific.");
        using var fixture = new ExternalProcessBoundaryTests.Fixture(sandboxEnabled: true);
        _ = Tools(fixture);
        await using var service = new McpService(fixture.Context, fixture.Root);
        await service.ConnectAsync([McpResourceServerFixture.ServerConfig("owned", false, "--file-tools")],
            TestContext.Current.CancellationToken);
        var status = Assert.Single(service.Status());
        Assert.False(status.Connected);
        Assert.Contains("macOS sandbox child processes", status.Error);
        Assert.Equal(0, status.ToolCount);
        Assert.Equal(fixture.InitialSettings, await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
    }

    private static ToolRuntime Tools(ExternalProcessBoundaryTests.Fixture fixture)
    {
        _ = new SystemPrompt(fixture.Context, new SystemPromptConfig());
        _ = new FileAttachmentStore(fixture.Context);
        return new ToolRuntime(fixture.Context);
    }

    private static async Task Connect(McpService service, bool expectWritableCwdRefusal = false)
    {
        await service.ConnectAsync([McpResourceServerFixture.ServerConfig("owned", false, "--file-tools")], TestContext.Current.CancellationToken);
        var status = Assert.Single(service.Status());
        if (expectWritableCwdRefusal)
        {
            Assert.False(status.Connected);
            Assert.Contains("Windows direct writable-directory sandboxes", status.Error);
            Assert.Equal(0, status.ToolCount);
            return;
        }
        Assert.True(status.Connected, status.Error);
        Assert.Equal(3, status.ToolCount);
    }

    private static Task<ToolExecutionResult> Execute(ToolRuntime tools, string name, object arguments, CancellationToken signal = default)
        => tools.Execute(new ToolExecutionInput
        {
            Name = $"mcp_owned_{name}",
            CallId = ToolCallId.Create($"owned-{Guid.NewGuid():N}"),
            Arguments = JsonSerializer.SerializeToElement(arguments),
            Signal = signal == default ? TestContext.Current.CancellationToken : signal,
        });

    private static string Text(ToolExecutionResult result)
        => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));
}
