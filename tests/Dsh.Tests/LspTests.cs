using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Lsp;
using Dsh.Lsp.Analysis;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;
using Dsh.Lsp.Server;

namespace Dsh.Tests;

public sealed class LspTests
{
    [Fact(Timeout = 30000)]
    public async Task EnhancedServer_InitializeOpenAndDocumentSymbol()
    {
        var input = new MemoryStream();
        await WriteAsync(input, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject { ["processId"] = 1, ["capabilities"] = new JsonObject() },
        });
        await WriteAsync(input, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "textDocument/didOpen",
            ["params"] = new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = "file:///a.cs",
                    ["languageId"] = "csharp",
                    ["version"] = 1,
                    ["text"] = "class C {}",
                },
            },
        });
        await WriteAsync(input, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "textDocument/documentSymbol",
            ["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = "file:///a.cs" } },
        });
        await WriteAsync(input, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 3,
            ["method"] = "shutdown",
        });
        await WriteAsync(input, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "exit",
        });
        input.Position = 0;

        using var output = new MemoryStream();
        await using var server = new LspServer(new FakeBackend());
        await server.RunAsync(input, output, TestContext.Current.CancellationToken);
        output.Position = 0;
        var messages = new List<JsonElement>();
        while (await LspFrameCodec.ReadAsync(output, TestContext.Current.CancellationToken) is { } frame)
            messages.Add(frame);

        var initialize = messages.Single(message => message.GetProperty("id").GetInt32() == 1)
            .GetProperty("result");
        Assert.True(initialize.GetProperty("capabilities").GetProperty("documentSymbolProvider").GetBoolean());
        Assert.True(initialize.GetProperty("capabilities").GetProperty("semanticTokensProvider").GetProperty("full").GetBoolean());
        var symbols = messages.Single(message => message.GetProperty("id").GetInt32() == 2)
            .GetProperty("result");
        Assert.Equal("C", Assert.Single(symbols.EnumerateArray()).GetProperty("name").GetString());
    }

    private static Task WriteAsync(Stream stream, JsonObject message)
        => LspFrameCodec.WriteAsync(stream, message, TestContext.Current.CancellationToken);

    private sealed class FakeBackend : ILspServerBackend
    {
        public Task<DocumentAnalysis> AnalyzeAsync(
            string uri,
            string languageId,
            string text,
            CancellationToken cancellationToken = default)
        {
            var document = LspDocument.Create(uri, languageId, text);
            var node = new SyntaxNode(
                "C",
                null,
                5,
                new LspRange(new LspPosition(0, 0), new LspPosition(0, 10)),
                new LspRange(new LspPosition(0, 6), new LspPosition(0, 7)),
                [],
                []);
            var syntax = new SyntaxSnapshot(document.Uri, document.Version, document.LanguageId, [node], [], [], []);
            var capabilities = LspCapabilityParser.Parse(JsonDocument.Parse("""{"capabilities":{}}""").RootElement);
            return Task.FromResult(new DocumentAnalysis(
                document,
                capabilities,
                syntax,
                new SymbolGraph(document.Uri, document.Version, [], []),
                ScopeGraphBuilder.Build(document, syntax),
                HierarchyGraph.Empty(document.Uri, document.Version),
                TypeFactsSnapshot.Empty,
                [],
                ValidationResult.Empty));
        }

        public Task<IReadOnlyList<LspLocation>> DefinitionAsync(string uri, LspPosition position, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LspLocation>>([]);

        public Task<IReadOnlyList<LspLocation>> DeclarationAsync(string uri, LspPosition position, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LspLocation>>([]);

        public Task<IReadOnlyList<LspLocation>> TypeDefinitionAsync(string uri, LspPosition position, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LspLocation>>([]);

        public Task<IReadOnlyList<LspLocation>> ImplementationAsync(string uri, LspPosition position, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LspLocation>>([]);

        public Task<IReadOnlyList<LspLocation>> ReferencesAsync(string uri, LspPosition position, bool includeDeclaration, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LspLocation>>([]);

        public Task<string?> HoverAsync(string uri, LspPosition position, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("class C");

        public Task<IReadOnlyList<SelectionChain>> SelectionChainsAsync(string uri, IReadOnlyList<LspPosition> positions, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SelectionChain>>([]);

        public Task<RenameTarget?> PrepareRenameAsync(string uri, LspPosition position, CancellationToken cancellationToken = default)
            => Task.FromResult<RenameTarget?>(null);

        public Task<WorkspaceEditFact?> RenameAsync(string uri, LspPosition position, string newName, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkspaceEditFact?>(null);

        public Task<IReadOnlyList<DocumentHighlightFact>> HighlightsAsync(string uri, LspPosition position, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DocumentHighlightFact>>([]);

        public Task<IReadOnlyList<CodeActionFact>> CodeActionsAsync(
            string uri,
            LspRange range,
            IReadOnlyList<LspDiagnostic> diagnostics,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CodeActionFact>>([]);

        public Task<CodeActionFact?> ResolveCodeActionAsync(
            string uri,
            JsonElement action,
            CancellationToken cancellationToken = default)
            => Task.FromResult<CodeActionFact?>(null);

        public Task<IReadOnlyList<WorkspaceSymbolFact>> WorkspaceSymbolsAsync(string languageId, string query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceSymbolFact>>([]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
