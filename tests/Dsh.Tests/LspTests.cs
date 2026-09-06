using System.Text.Json;
using Dsh.Lsp;
using Dsh.Sdk;

namespace Dsh.Tests;

public sealed class LspTests
{
    [Fact]
    public async Task Initialize_ReturnsCapabilitiesAndServerInfo()
    {
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":"1","method":"initialize","params":{"processId":1,"capabilities":{}}}
            """);
        var output = new StringWriter();
        await using var transport = new JsonRpcLineTransport(input, output);
        var server = new LspServer();
        transport.RequestHandler = server.HandleRequestAsync;
        transport.Start();
        await WaitUntilAsync(() => output.ToString().Contains("dsh-lsp"));
        var line = output.ToString().TrimEnd();
        using var document = JsonDocument.Parse(line);
        var result = document.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("capabilities").GetProperty("hoverProvider").GetBoolean());
        Assert.Equal("dsh-lsp", result.GetProperty("serverInfo").GetProperty("name").GetString());
        await transport.StopAsync();
    }

    [Fact]
    public async Task Hover_ReturnsPlainText()
    {
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":"2","method":"textDocument/hover","params":{"textDocument":{"uri":"file:///a.cs"},"position":{"line":0,"character":0}}}
            """);
        var output = new StringWriter();
        await using var transport = new JsonRpcLineTransport(input, output);
        var server = new LspServer();
        transport.RequestHandler = server.HandleRequestAsync;
        transport.Start();
        await WaitUntilAsync(() => output.ToString().Contains("DeepSeek Harness LSP"));
        var line = output.ToString().TrimEnd();
        using var document = JsonDocument.Parse(line);
        var result = document.RootElement.GetProperty("result");
        Assert.Equal("plaintext", result.GetProperty("contents").GetProperty("kind").GetString());
        await transport.StopAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("condition was not met before the timeout");
            await Task.Delay(10);
        }
    }
}
