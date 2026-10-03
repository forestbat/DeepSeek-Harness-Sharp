using System.IO.Pipes;
using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Mcp;
using Dsh.Runtime;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dsh.Tests.Mcp;

public sealed class McpToolBridgeTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        { "type": "object", "properties": { "path": { "type": "string" } }, "required": ["path"] }
        """).RootElement.Clone();

    private static ToolDefinition WrapEcho(
        Func<IReadOnlyDictionary<string, object?>?, CancellationToken, ValueTask<CallToolResult>> call)
        => McpToolBridge.Wrap("Rider MCP", "ide-read_file", null, Schema, call);

    private static ToolRunContext RunContext(JsonElement args)
        => new()
        {
            CallId = ToolCallId.Create("call_1"),
            RootCallIdValue = ToolCallId.Create("call_1"),
            Name = "mcp_rider_mcp_ide_read_file",
            Arguments = args,
            Signal = TestContext.Current.CancellationToken,
        };

    [Fact]
    public void Wrap_PrefixesAndSanitizesNames()
    {
        var tool = WrapEcho((_, _) => throw new InvalidOperationException());

        Assert.Equal("mcp_Rider_MCP_ide_read_file", tool.Name);
    }

    [Fact]
    public void Wrap_FallsBackToToolNameWhenDescriptionMissing()
    {
        var tool = WrapEcho((_, _) => throw new InvalidOperationException());

        Assert.Equal("ide-read_file", tool.Description);
    }

    [Fact]
    public void Wrap_PassesThroughInputSchema()
    {
        var tool = WrapEcho((_, _) => throw new InvalidOperationException());

        Assert.Equal("string", tool.Parameters["properties"]!["path"]!["type"]!.GetValue<string>());
        Assert.Equal("path", Assert.Single(tool.Parameters["required"]!.AsArray())!.GetValue<string>());
    }

    [Fact]
    public async Task Execute_ForwardsArgumentsAndReturnsSerializedResult()
    {
        IReadOnlyDictionary<string, object?>? captured = null;
        var tool = WrapEcho((arguments, _) =>
        {
            captured = arguments;
            return ValueTask.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "file content" }],
            });
        });
        var args = JsonDocument.Parse("""{ "path": "/tmp/a.txt" }""").RootElement;

        var value = Assert.IsType<JsonElement>(await tool.Execute(args, RunContext(args)));

        Assert.Equal("/tmp/a.txt", captured!["path"]!.ToString());
        Assert.Equal("file content", value.GetProperty("content")[0].GetProperty("text").GetString());
        var rendered = Assert.IsType<TextBlock>(Assert.Single(tool.Output.Render(args, value)));
        Assert.Equal("file content", rendered.Text);
    }

    [Fact]
    public async Task Execute_ThrowsHarnessExceptionWhenServerReportsError()
    {
        var tool = WrapEcho((_, _) => ValueTask.FromResult(new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "boom" }],
        }));
        var args = JsonDocument.Parse("""{ "path": "/tmp/a.txt" }""").RootElement;

        var error = await Assert.ThrowsAsync<HarnessException>(() => tool.Execute(args, RunContext(args)));

        Assert.Equal(McpToolBridge.ErrorCode, error.Code);
        Assert.Contains("boom", error.Message);
    }

    [Fact]
    public void Render_FallsBackToRawJsonWithoutTextBlocks()
    {
        var tool = WrapEcho((_, _) => throw new InvalidOperationException());
        var value = JsonDocument.Parse("""{ "content": [{ "type": "image", "data": "AAAA" }] }""").RootElement;

        var rendered = Assert.IsType<TextBlock>(Assert.Single(tool.Output.Render(JsonDocument.Parse("{}").RootElement, value)));

        Assert.Contains("image", rendered.Text);
    }
}

public sealed class McpServiceTests
{
    [Fact]
    public async Task ConnectAsync_RegistersServerToolsAsNativeTools()
    {
        using var serverOut = new AnonymousPipeServerStream(PipeDirection.Out);
        using var serverIn = new AnonymousPipeServerStream(PipeDirection.In);
        using var clientOut = new AnonymousPipeClientStream(PipeDirection.Out, serverIn.ClientSafePipeHandle);
        using var clientIn = new AnonymousPipeClientStream(PipeDirection.In, serverOut.ClientSafePipeHandle);
        var echo = McpServerTool.Create((string text) => $"echo:{text}",
            new McpServerToolCreateOptions { Name = "echo", Description = "Echoes text." });
        var server = McpServer.Create(
            new StreamServerTransport(serverIn, serverOut, "loopback"),
            new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "loopback", Version = "1.0" },
                ToolCollection = [echo],
            });
        await using var serverLifetime = server;
        _ = server.RunAsync(TestContext.Current.CancellationToken);

        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        await using var service = new McpService(ctx);
        await service.ConnectAsync(new McpServerConfig("loopback", "stdio"),
            new StreamClientTransport(clientOut, clientIn), TestContext.Current.CancellationToken);

        var status = Assert.Single(service.Status());
        Assert.True(status.Connected, status.Error);
        Assert.Equal(1, status.ToolCount);
        var registered = tools.Get("mcp_loopback_echo");
        Assert.NotNull(registered);
        var args = JsonDocument.Parse("""{ "text": "hello" }""").RootElement;
        var value = Assert.IsType<JsonElement>(await registered.Execute(args, new ToolRunContext
        {
            CallId = ToolCallId.Create("call_1"),
            RootCallIdValue = ToolCallId.Create("call_1"),
            Name = "mcp_loopback_echo",
            Arguments = args,
            Signal = TestContext.Current.CancellationToken,
        }));
        var rendered = Assert.IsType<TextBlock>(Assert.Single(registered.Output.Render(args, value)));
        Assert.Contains("echo:hello", rendered.Text);
    }
}
