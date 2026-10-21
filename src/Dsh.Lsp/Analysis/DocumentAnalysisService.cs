using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Analysis;

/** Coordinates the layered analysis for one open document. */
public sealed class DocumentAnalysisService(LspClientSession session, DocumentAnalysisOptions? options = null)
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
        var symbols = await new SymbolGraphBuilder(session, capabilities)
            .BuildAsync(document, syntax, _options, cancellationToken)
            .ConfigureAwait(false);
        var types = await new TypeFactExtractor(session, capabilities)
            .ExtractAsync(document, syntax, _options, cancellationToken)
            .ConfigureAwait(false);
        var validation = SnapshotValidator.Validate(document, syntax);
        return new DocumentAnalysis(document, capabilities, syntax, symbols, types, diagnostics, validation);
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

        var result = await session.RequestAsync("textDocument/diagnostic", new JsonObject
        {
            ["textDocument"] = LspJson.TextDocumentNode(document.Uri),
        }, cancellationToken).ConfigureAwait(false);
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
