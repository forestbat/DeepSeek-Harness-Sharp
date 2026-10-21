using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;
using Dsh.Lsp.TreeSitter;

namespace Dsh.Lsp.Analysis;

/** Coordinates the layered analysis for one open document. */
public sealed class DocumentAnalysisService(
    LspClientSession session,
    DocumentAnalysisOptions? options = null,
    ISyntaxTreeProvider? syntaxTreeProvider = null,
    IStructuredTypeProvider? structuredTypeProvider = null,
    IFlowProvider? flowProvider = null)
{
    private readonly DocumentAnalysisOptions _options = options ?? DocumentAnalysisOptions.Default;

    public async Task<DocumentSnapshot> BuildSnapshotAsync(
        LspDocument document,
        CancellationToken cancellationToken = default)
    {
        var capabilities = session.Capabilities;
        var diagnostics = await CollectDiagnosticsAsync(document, capabilities, cancellationToken)
            .ConfigureAwait(false);
        var syntax = await new SyntaxSnapshotBuilder(session, capabilities)
            .BuildShallowAsync(document, diagnostics, cancellationToken)
            .ConfigureAwait(false);
        if (syntax.Roots.Count == 0
            && syntaxTreeProvider is not null
            && await syntaxTreeProvider.TryParseAsync(document, cancellationToken).ConfigureAwait(false) is { } parserTree)
        {
            syntax = syntax with
            {
                Cst = parserTree,
                ParserRoots = [CstToSyntaxConverter.Convert(parserTree, document.Version)],
                SourceKind = "lsp+parser",
                IsPartial = false,
            };
        }
        return new DocumentSnapshot(
            document,
            capabilities,
            syntax,
            diagnostics,
            SnapshotValidator.Validate(document, syntax));
    }

    public Task<IReadOnlyList<SemanticToken>> GetSemanticTokensAsync(
        LspDocument document,
        CancellationToken cancellationToken = default)
        => new SyntaxSnapshotBuilder(session, session.Capabilities)
            .GetSemanticTokensAsync(document, cancellationToken);

    public async Task<SyntaxSnapshot> WithCstAsync(
        LspDocument document,
        SyntaxSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        if (syntaxTreeProvider is null
            || await syntaxTreeProvider.TryParseAsync(document, cancellationToken).ConfigureAwait(false) is not { } cst)
            return snapshot;
        return snapshot with
        {
            Cst = cst,
            SourceKind = "lsp+parser",
            ParserRoots = [CstToSyntaxConverter.Convert(cst, document.Version)],
        };
    }

    public async Task<SyntaxSnapshot> WithSelectionChainsAsync(
        LspDocument document,
        SyntaxSnapshot snapshot,
        IReadOnlyList<LspPosition> positions,
        CancellationToken cancellationToken = default)
    {
        var chains = await GetSelectionChainsAsync(document, positions, cancellationToken).ConfigureAwait(false);
        return snapshot with { SelectionChains = chains };
    }

    public Task<SymbolGraph> BuildSymbolsAsync(
        DocumentSnapshot snapshot,
        CancellationToken cancellationToken = default)
        => new SymbolGraphBuilder(session, snapshot.Capabilities)
            .BuildAsync(snapshot.Document, snapshot.Syntax, _options, cancellationToken);

    public ScopeGraph BuildScopes(DocumentSnapshot snapshot)
        => ScopeGraphBuilder.Build(snapshot.Document, snapshot.Syntax);

    public Task<HierarchyGraph> BuildHierarchyAsync(
        DocumentSnapshot snapshot,
        CancellationToken cancellationToken = default)
        => new HierarchyGraphBuilder(session, snapshot.Capabilities)
            .BuildAsync(snapshot.Document, snapshot.Syntax, _options, cancellationToken);

    public Task<TypeFactsSnapshot> ExtractTypesAsync(
        DocumentSnapshot snapshot,
        CancellationToken cancellationToken = default)
        => new TypeFactExtractor(session, snapshot.Capabilities, structuredTypeProvider)
            .ExtractAsync(snapshot.Document, snapshot.Syntax, _options, cancellationToken);

    public Task<FlowGraph?> GetFlowAsync(
        LspDocument document,
        LspRange range,
        CancellationToken cancellationToken = default)
        => flowProvider is null
            ? Task.FromResult<FlowGraph?>(null)
            : flowProvider.BuildAsync(document, range, cancellationToken);

    public async Task<IReadOnlyList<SelectionChain>> GetSelectionChainsAsync(
        LspDocument document,
        IReadOnlyList<LspPosition> positions,
        CancellationToken cancellationToken = default)
        => await new SelectionChainBuilder(session, session.Capabilities)
            .BuildAsync(document, positions, cancellationToken)
            .ConfigureAwait(false);

    private async Task<IReadOnlyList<LspDiagnostic>> CollectDiagnosticsAsync(
        LspDocument document,
        LspCapabilityProfile capabilities,
        CancellationToken cancellationToken)
    {
        var pushed = session.Diagnostics(document.Uri)?.Items ?? [];
        if (!capabilities.PullDiagnostics)
            return pushed;

        JsonElement? result;
        try
        {
            result = await session.PullDiagnosticsAsync(document.Uri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is LspResponseException or LspRequestTimeoutException)
        {
            return pushed;
        }
        var pulled = ParsePullDiagnostics(result);
        if (pulled.Count == 0)
            return pushed;
        return pushed
            .Concat(pulled)
            .DistinctBy(diagnostic => (diagnostic.Range, diagnostic.Code, diagnostic.Message))
            .ToArray();
    }

    private static IReadOnlyList<LspDiagnostic> ParsePullDiagnostics(JsonElement? result)
    {
        if (result is not { } value)
            return [];
        return LspJson.String(value, "kind") == "unchanged"
            ? []
            : LspDiagnostics.ParseMany(LspJson.Property(value, "items"));
    }
}
