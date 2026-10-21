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
    ISyntaxTreeProvider? syntaxTreeProvider = null)
{
    private readonly DocumentAnalysisOptions _options = options ?? DocumentAnalysisOptions.Default;

    public async Task<DocumentAnalysis> AnalyzeAsync(
        LspDocument document,
        CancellationToken cancellationToken = default)
    {
        var capabilities = session.Capabilities;
        var diagnostics = await CollectDiagnosticsAsync(document, capabilities, cancellationToken)
            .ConfigureAwait(false);
        var syntax = await new SyntaxSnapshotBuilder(session, capabilities)
            .BuildAsync(document, diagnostics, cancellationToken)
            .ConfigureAwait(false);
        if (syntaxTreeProvider is not null
            && await syntaxTreeProvider.TryParseAsync(document, cancellationToken).ConfigureAwait(false) is { } cst)
            syntax = syntax with { Cst = cst };
        var symbols = await new SymbolGraphBuilder(session, capabilities)
            .BuildAsync(document, syntax, _options, cancellationToken)
            .ConfigureAwait(false);
        var scopes = ScopeGraphBuilder.Build(document, syntax);
        var hierarchy = await new HierarchyGraphBuilder(session, capabilities)
            .BuildAsync(document, syntax, _options, cancellationToken)
            .ConfigureAwait(false);
        var types = await new TypeFactExtractor(session, capabilities)
            .ExtractAsync(document, syntax, _options, cancellationToken)
            .ConfigureAwait(false);
        var validation = SnapshotValidator.Validate(document, syntax);
        return new DocumentAnalysis(document, capabilities, syntax, symbols, scopes, hierarchy, types, diagnostics, validation);
    }

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
