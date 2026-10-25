using System.Text.Json;
using Dsh.Lsp;
using Dsh.Sdk;

namespace Dsh.Tests;

public sealed class LspTests
{
    [Fact(Timeout = 5000)]
    public async Task Initialize_ReturnsCapabilitiesAndServerInfo()
    {
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":"1","method":"initialize","params":{"processId":1,"capabilities":{}}}
            """);
        var output = new JsonRpcLineCapture();
        await using var transport = new JsonRpcLineTransport(input, output);
        var server = new LspServer();
        transport.RequestHandler = server.HandleRequestAsync;
        transport.Start();
        using var document = await output.NextMessageAsync(TestContext.Current.CancellationToken);
        var result = document.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("capabilities").GetProperty("hoverProvider").GetBoolean());
        Assert.Equal("dsh-lsp", result.GetProperty("serverInfo").GetProperty("name").GetString());
        await transport.StopAsync();
    }

    [Fact(Timeout = 5000)]
    public async Task Hover_ReturnsPlainText()
    {
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":"2","method":"textDocument/hover","params":{"textDocument":{"uri":"file:///a.cs"},"position":{"line":0,"character":0}}}
            """);
        var output = new JsonRpcLineCapture();
        await using var transport = new JsonRpcLineTransport(input, output);
        var server = new LspServer();
        transport.RequestHandler = server.HandleRequestAsync;
        transport.Start();
        using var document = await output.NextMessageAsync(TestContext.Current.CancellationToken);
        var result = document.RootElement.GetProperty("result");
        Assert.Equal("plaintext", result.GetProperty("contents").GetProperty("kind").GetString());
        await transport.StopAsync();
    }

}
