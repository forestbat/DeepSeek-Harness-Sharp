using System.IO.Pipes;
using System.Net;
using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Mcp;
using Dsh.Runtime;
using ModelContextProtocol;
using ModelContextProtocol.Client;
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

public sealed class McpStaleSessionTests
{
    [Fact]
    public void IsStale_HttpRequestNotFound() =>
        Assert.True(McpStaleSession.IsStale(
            new HttpRequestException("Streamable HTTP session not found", null, HttpStatusCode.NotFound),
            CancellationToken.None));

    [Fact]
    public void IsStale_OperationCanceledWithoutUserAbort() =>
        Assert.True(McpStaleSession.IsStale(new OperationCanceledException(), CancellationToken.None));

    [Fact]
    public void IsStale_OperationCanceledWithUserAbort()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.False(McpStaleSession.IsStale(new OperationCanceledException(), cts.Token));
    }

    [Fact]
    public void IsStale_SessionNotFoundMessage() =>
        Assert.True(McpStaleSession.IsStale(new McpException("HTTP 404 session not found"), CancellationToken.None));

    [Fact]
    public void IsStale_ToolErrorIsNotStale() =>
        Assert.False(McpStaleSession.IsStale(
            new HarnessException("mcp tool failed", McpToolBridge.ErrorCode),
            CancellationToken.None));
}

public sealed class McpReconnectRetryTests
{
    private static CallToolResult Ok() => new() { Content = [new TextContentBlock { Text = "ok" }] };

    [Fact]
    public async Task StaleFailure_ReconnectsOnceAndRetries()
    {
        var calls = 0;
        var reconnects = new List<long>();

        var result = await McpToolBridge.CallWithReconnectAsync(
            invoke: () =>
            {
                calls++;
                return calls == 1
                    ? throw new HttpRequestException("session not found", null, HttpStatusCode.NotFound)
                    : ValueTask.FromResult(Ok());
            },
            usedVersion: () => 7,
            reconnect: version =>
            {
                reconnects.Add(version);
                return Task.CompletedTask;
            },
            signal: CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal([7L], reconnects);
        Assert.Equal("ok", Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
    }

    [Fact]
    public async Task StaleFailure_RetriesAtMostOnce()
    {
        var calls = 0;

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await McpToolBridge.CallWithReconnectAsync(
                invoke: () =>
                {
                    calls++;
                    throw new HttpRequestException("session not found", null, HttpStatusCode.NotFound);
                },
                usedVersion: () => 1,
                reconnect: _ => Task.CompletedTask,
                signal: CancellationToken.None));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task UserAbort_IsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var calls = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await McpToolBridge.CallWithReconnectAsync(
                invoke: () =>
                {
                    calls++;
                    throw new OperationCanceledException(cts.Token);
                },
                usedVersion: () => 1,
                reconnect: _ => Task.CompletedTask,
                signal: cts.Token));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ToolError_IsNotRetried()
    {
        var calls = 0;

        await Assert.ThrowsAsync<HarnessException>(async () =>
            await McpToolBridge.CallWithReconnectAsync(
                invoke: () =>
                {
                    calls++;
                    throw new HarnessException("mcp tool failed", McpToolBridge.ErrorCode);
                },
                usedVersion: () => 1,
                reconnect: _ => Task.CompletedTask,
                signal: CancellationToken.None));

        Assert.Equal(1, calls);
    }
}

public sealed class McpServerHandleTests
{
    [Fact]
    public async Task Reconnect_RehandshakesAndGatesOnVersion()
    {
        var lifetime = new Lifetime();
        var handle = new McpServerHandle(new McpServerConfig("loopback", "stdio"), lifetime.Factory);
        await using (handle)
        {
            await handle.ConnectAsync(TestContext.Current.CancellationToken);
            var first = handle.Version;
            Assert.True(handle.Connected, handle.Status.Error);
            Assert.Equal(1, handle.ToolCount);

            await handle.ReconnectIfVersionAsync(first, TestContext.Current.CancellationToken);
            Assert.True(handle.Connected, handle.Status.Error);
            Assert.True(handle.Version > first);
            Assert.Equal(1, handle.ToolCount);

            var current = handle.Version;
            await handle.ReconnectIfVersionAsync(first, TestContext.Current.CancellationToken);
            Assert.Equal(current, handle.Version);
        }
        await lifetime.DisposeAsync();
    }

    private sealed class Lifetime : IAsyncDisposable
    {
        private readonly List<IDisposable> _streams = [];
        private readonly List<IAsyncDisposable> _servers = [];

        public IClientTransport Factory()
        {
            var serverOut = new AnonymousPipeServerStream(PipeDirection.Out);
            var serverIn = new AnonymousPipeServerStream(PipeDirection.In);
            var clientOut = new AnonymousPipeClientStream(PipeDirection.Out, serverIn.ClientSafePipeHandle);
            var clientIn = new AnonymousPipeClientStream(PipeDirection.In, serverOut.ClientSafePipeHandle);
            var echo = McpServerTool.Create((string text) => $"echo:{text}",
                new McpServerToolCreateOptions { Name = "echo", Description = "Echoes text." });
            var server = McpServer.Create(
                new StreamServerTransport(serverIn, serverOut, "loopback"),
                new McpServerOptions
                {
                    ServerInfo = new Implementation { Name = "loopback", Version = "1.0" },
                    ToolCollection = [echo],
                });
            _ = server.RunAsync(TestContext.Current.CancellationToken);
            _servers.Add(server);
            _streams.AddRange([serverOut, serverIn, clientOut, clientIn]);
            return new StreamClientTransport(clientOut, clientIn);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var server in _servers)
            {
                try
                {
                    await server.DisposeAsync();
                }
                catch
                {
                    // Teardown only: a server whose transport was already closed may throw.
                }
            }
            foreach (var stream in _streams)
                stream.Dispose();
        }
    }
}
