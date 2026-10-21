using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Lsp.Analysis;
using Dsh.Lsp.Catalog;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Tests;

public sealed class LspStructuredTests
{
    [Fact]
    public void Catalog_HasThirtyProfilesWithUniqueIds()
    {
        Assert.Equal(30, LanguageCatalog.All.Count);
        Assert.Equal(30, LanguageCatalog.All.Select(profile => profile.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var profile in LanguageCatalog.All)
        {
            Assert.NotEmpty(profile.LanguageIds);
            Assert.NotEmpty(profile.Servers);
            Assert.NotEmpty(profile.Extensions.Concat(profile.FileNames));
        }
    }

    [Fact]
    public void Catalog_ResolvesCommonPaths()
    {
        Assert.Equal("typescript", LanguageCatalog.ResolveByPath("src/app.tsx").Id);
        Assert.Equal("csharp", LanguageCatalog.ResolveByPath("src/Program.cs").Id);
        Assert.Equal("cpp", LanguageCatalog.ResolveByPath("src/main.hpp").Id);
        Assert.Equal("c", LanguageCatalog.ResolveByPath("src/main.h").Id);
        Assert.Equal("markdown", LanguageCatalog.ResolveByPath("README.md").Id);
        Assert.Equal("json", LanguageCatalog.ResolveByPath("settings.jsonc").Id);
        Assert.Equal("ruby", LanguageCatalog.ResolveByPath("Gemfile").Id);
        Assert.Equal("powershell", LanguageCatalog.ResolveByPath("build.ps1").Id);
    }

    [Fact]
    public void Document_UsesUtf16PositionsAndIncrementalChanges()
    {
        var document = LspDocument.Create("file:///sample.cs", "csharp", "a\r\nbc\n");
        Assert.Equal(new LspPosition(1, 0), document.PositionAt(3));
        Assert.Equal(4, document.OffsetAt(new LspPosition(1, 1)));
        Assert.Equal(1, document.LineLength(0));
        Assert.Equal(2, document.LineLength(1));

        var update = document.Apply([LspTextChange.Replace(new LspRange(new LspPosition(0, 0), new LspPosition(0, 1)), "hello")]);
        Assert.Equal("hello\r\nbc\n", update.Document.Text);
        Assert.Equal(2, update.Document.Version);
        Assert.Equal(new LspPosition(0, 0), update.ChangedRange.Start);
    }

    [Fact]
    public void DocumentUri_RoundTripsWithoutBackslashes()
    {
        var path = Path.Combine(Path.GetTempPath(), "dsh lsp", "sample.cs");
        var uri = LspDocumentUri.FromPath(path);
        Assert.True(Uri.IsWellFormedUriString(uri, UriKind.Absolute));
        Assert.DoesNotContain('\\', uri);
        Assert.True(LspDocumentUri.TryGetPath(uri, out var roundTripped));
        Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(roundTripped));
    }

    [Fact(Timeout = 30000)]
    public async Task SyntaxBuilder_ReconstructsHierarchyFoldingAndTokens()
    {
        var channel = new FixtureChannel(
            ("textDocument/documentSymbol", """
                [{"name":"Demo","kind":3,"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":48}},"selectionRange":{"start":{"line":0,"character":0},"end":{"line":0,"character":4}},"children":[
                  {"name":"C","kind":5,"range":{"start":{"line":0,"character":14},"end":{"line":0,"character":46}},"selectionRange":{"start":{"line":0,"character":20},"end":{"line":0,"character":21}}}
                ]}]
                """),
            ("textDocument/foldingRange", """
                [{"startLine":0,"endLine":0,"kind":"region"}]
                """),
            ("textDocument/semanticTokens/full", """
                {"data":[0,0,4,0,1,0,14,1,1,0]}
                """));

        var capabilities = Capabilities("""
            {"capabilities":{"documentSymbolProvider":true,"foldingRangeProvider":true,"semanticTokensProvider":{"legend":{"tokenTypes":["namespace","class"],"tokenModifiers":["declaration"]}}}}
            """);
        var document = LspDocument.Create("file:///demo.cs", "csharp", "namespace Demo { class C { void M() {} } }");
        var snapshot = await new SyntaxSnapshotBuilder(channel, capabilities)
            .BuildAsync(document, [], TestContext.Current.CancellationToken);

        var root = Assert.Single(snapshot.Roots);
        Assert.Equal("Demo", root.Name);
        Assert.Equal("C", Assert.Single(root.Children).Name);
        Assert.Equal(2, snapshot.SemanticTokens.Count);
        var token = snapshot.SemanticTokens[0];
        Assert.Equal("namespace", token.Type);
        Assert.Contains("declaration", token.Modifiers);
        Assert.Equal("class", snapshot.SemanticTokens[1].Type);
        Assert.Equal("region", Assert.Single(snapshot.FoldingRanges).Kind);
    }

    [Fact(Timeout = 30000)]
    public async Task SyntaxBuilder_ParsesFlatSymbolInformation()
    {
        var channel = new FixtureChannel(("textDocument/documentSymbol", """
            [{"name":"M","kind":6,"location":{"uri":"file:///demo.cs","range":{"start":{"line":0,"character":14},"end":{"line":0,"character":17}}},"containerName":"C"}]
            """));
        var capabilities = Capabilities("""{"capabilities":{"documentSymbolProvider":true}}""");
        var document = LspDocument.Create("file:///demo.cs", "csharp", "namespace Demo { class C { void M() {} } }");
        var snapshot = await new SyntaxSnapshotBuilder(channel, capabilities)
            .BuildAsync(document, [], TestContext.Current.CancellationToken);
        var node = Assert.Single(snapshot.Roots);
        Assert.Equal("M", node.Name);
        Assert.Equal("C", node.Detail);
        Assert.Empty(node.Children);
    }

    [Fact(Timeout = 30000)]
    public async Task SymbolGraph_ResolvesDefinitionAndReferences()
    {
        var channel = new FixtureChannel(
            ("textDocument/definition", """
                {"uri":"file:///other.cs","range":{"start":{"line":1,"character":2},"end":{"line":1,"character":5}}}
                """),
            ("textDocument/references", """
                [{"uri":"file:///other.cs","range":{"start":{"line":2,"character":2},"end":{"line":2,"character":5}}}]
                """));
        var capabilities = Capabilities("""{"capabilities":{"definitionProvider":true,"referencesProvider":true}}""");
        var document = LspDocument.Create("file:///demo.cs", "csharp", "class C {}");
        var node = new SyntaxNode(
            "C",
            null,
            5,
            new LspRange(new LspPosition(0, 0), new LspPosition(0, 9)),
            new LspRange(new LspPosition(0, 6), new LspPosition(0, 7)),
            [],
            []);
        var snapshot = new SyntaxSnapshot(document.Uri, document.Version, document.LanguageId, [node], [], [], []);
        var graph = await new SymbolGraphBuilder(channel, capabilities)
            .BuildAsync(document, snapshot, DocumentAnalysisOptions.Default, TestContext.Current.CancellationToken);

        Assert.Single(graph.Nodes);
        Assert.Equal(2, graph.Edges.Count);
        Assert.Contains(graph.Edges, edge => edge.Kind == SymbolEdgeKind.Definition);
        Assert.Contains(graph.Edges, edge => edge.Kind == SymbolEdgeKind.Reference);
    }

    [Fact(Timeout = 30000)]
    public async Task TypeFacts_ParsesMarkupHoverAndInlayHints()
    {
        var channel = new FixtureChannel(
            ("textDocument/hover", """
                {"contents":{"kind":"markdown","value":"`int`"},"range":{"start":{"line":0,"character":6},"end":{"line":0,"character":7}}}
                """),
            ("textDocument/inlayHint", """
                [{"position":{"line":0,"character":7},"label":[{"value":": "},{"value":"int"}],"paddingLeft":false,"paddingRight":true}]
                """));
        var capabilities = Capabilities("""{"capabilities":{"hoverProvider":true,"inlayHintProvider":true}}""");
        Assert.True(capabilities.Hover);
        Assert.Equal("`int`", TypeFactExtractor.HoverText(ParseJson("""{"kind":"markdown","value":"`int`"}""")));
        Assert.Equal("`int`", TypeFactExtractor.HoverText(LspJson.Property(channel.Response("textDocument/hover"), "contents")));
        var document = LspDocument.Create("file:///demo.cs", "csharp", "class C {}");
        var node = new SyntaxNode(
            "C",
            null,
            5,
            new LspRange(new LspPosition(0, 0), new LspPosition(0, 9)),
            new LspRange(new LspPosition(0, 6), new LspPosition(0, 7)),
            [],
            []);
        var snapshot = new SyntaxSnapshot(document.Uri, document.Version, document.LanguageId, [node], [], [], []);
        var facts = await new TypeFactExtractor(channel, capabilities)
            .ExtractAsync(document, snapshot, DocumentAnalysisOptions.Default, TestContext.Current.CancellationToken);

        Assert.Single(snapshot.Nodes);
        Assert.Contains("textDocument/hover", channel.Calls);
        Assert.Equal("`int`", Assert.Single(facts.Facts).Display);
        Assert.Equal(": int", Assert.Single(facts.Hints).Label);
    }

    [Fact(Timeout = 30000)]
    public async Task SelectionChain_ReadsAncestorRanges()
    {
        var channel = new FixtureChannel(("textDocument/selectionRange", """
            [{
              "range":{"start":{"line":0,"character":6},"end":{"line":0,"character":7}},
              "parent":{
                "range":{"start":{"line":0,"character":0},"end":{"line":0,"character":9}},
                "parent":{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":11}}}
              }
            }]
            """));
        var capabilities = Capabilities("""{"capabilities":{"selectionRangeProvider":true}}""");
        var document = LspDocument.Create("file:///demo.cs", "csharp", "class C {}");
        var chains = await new SelectionChainBuilder(channel, capabilities)
            .BuildAsync(document, [new LspPosition(0, 6)], TestContext.Current.CancellationToken);
        var chain = Assert.Single(chains);
        Assert.Equal(3, chain.Ranges.Count);
        Assert.Equal(new LspPosition(0, 6), chain.Ranges[0].Start);
        Assert.Equal(new LspPosition(0, 0), chain.Ranges[^1].Start);
    }

    [Fact]
    public void Validator_FindsOutOfRangeAndParentViolations()
    {
        var document = LspDocument.Create("file:///demo.cs", "csharp", "abc");
        var child = new SyntaxNode(
            "b",
            null,
            13,
            new LspRange(new LspPosition(0, 5), new LspPosition(0, 6)),
            new LspRange(new LspPosition(0, 5), new LspPosition(0, 6)),
            [],
            []);
        var root = new SyntaxNode(
            "a",
            null,
            13,
            new LspRange(new LspPosition(0, 0), new LspPosition(0, 5)),
            new LspRange(new LspPosition(0, 0), new LspPosition(0, 5)),
            [],
            [child]);
        var snapshot = new SyntaxSnapshot(document.Uri, document.Version, document.LanguageId, [root], [], [], []);
        var result = SnapshotValidator.Validate(document, snapshot);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "child-outside-parent");
        Assert.Contains(result.Issues, issue => issue.Code == "syntax.range");
    }

    [Fact(Timeout = 30000)]
    public async Task FrameCodec_RoundTripsAFramedMessage()
    {
        await using var stream = new MemoryStream();
        await LspFrameCodec.WriteAsync(stream, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["result"] = new JsonObject { ["ok"] = true },
        }, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var message = await LspFrameCodec.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.NotNull(message);
        Assert.True(message.Value.GetProperty("result").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void CapabilityParser_ReadsStructuredFeatures()
    {
        var capabilities = LspCapabilityParser.Parse(ParseJson("""
            {"capabilities":{"textDocumentSync":2,"documentSymbolProvider":true,"selectionRangeProvider":true,"foldingRangeProvider":true,"semanticTokensProvider":{"full":{"delta":true},"legend":{"tokenTypes":["class"],"tokenModifiers":["declaration"]}},"renameProvider":{"prepareProvider":true},"diagnosticProvider":{},"workspace":{"symbolProvider":true}},"serverInfo":{"name":"fixture","version":"1"}}
            """));
        Assert.Equal(LspTextDocumentSyncKind.Incremental, capabilities.SyncKind);
        Assert.True(capabilities.DocumentSymbol);
        Assert.True(capabilities.SelectionRange);
        Assert.True(capabilities.SemanticTokensDelta);
        Assert.True(capabilities.PrepareRename);
        Assert.True(capabilities.PullDiagnostics);
        Assert.True(capabilities.WorkspaceSymbol);
        Assert.Equal("fixture", capabilities.ServerName);
        Assert.Equal("class", Assert.Single(capabilities.SemanticTokenTypes));
    }

    private static LspCapabilityProfile Capabilities(string json)
        => LspCapabilityParser.Parse(ParseJson(json));

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FixtureChannel : ILspRequestChannel
    {
        private readonly Dictionary<string, JsonElement> _responses;

        public FixtureChannel(params (string Method, string Json)[] responses)
        {
            _responses = responses.ToDictionary(
                response => response.Method,
                response => ParseJson(response.Json),
                StringComparer.Ordinal);
        }

        public Task<JsonElement?> RequestAsync(
            string method,
            JsonObject? parameters,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(method);
            return Task.FromResult<JsonElement?>(_responses.TryGetValue(method, out var response)
                ? response
                : null);
        }

        public JsonElement? Response(string method)
            => _responses.TryGetValue(method, out var response) ? response : null;

        public List<string> Calls { get; } = [];
    }
}
