using System.Text.Json;
using Dsh.Sdk;

namespace Dsh.Lsp;

public static class LspMethods
{
    public const string Initialize = "initialize";
    public const string Initialized = "initialized";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";
    public const string TextDocumentHover = "textDocument/hover";
}

public sealed class LspServer
{
    public Task<object?> HandleRequestAsync(string method, JsonElement? parameters)
    {
        return method switch
        {
            LspMethods.Initialize => Task.FromResult<object?>(new Dictionary<string, object?>
            {
                ["capabilities"] = new Dictionary<string, object?>
                {
                    ["textDocumentSync"] = 1,
                    ["hoverProvider"] = true,
                },
                ["serverInfo"] = new Dictionary<string, object?>
                {
                    ["name"] = "dsh-lsp",
                    ["version"] = "0.0.1",
                },
            }),
            LspMethods.Shutdown => Task.FromResult<object?>(null),
            LspMethods.TextDocumentHover => Task.FromResult<object?>(new Dictionary<string, object?>
            {
                ["contents"] = new Dictionary<string, object?>
                {
                    ["kind"] = "plaintext",
                    ["value"] = "DeepSeek Harness LSP",
                },
            }),
            _ => throw new InvalidOperationException($"unknown LSP method: {method}"),
        };
    }
}
