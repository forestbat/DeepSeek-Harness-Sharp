using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Model;

public sealed record ZoneView(
    LanguageZone Zone,
    ZoneMapping Mapping,
    DocumentSnapshot Snapshot,
    SymbolGraph? Symbols = null,
    TypeFactsSnapshot? Types = null)
{
    public IReadOnlyList<LspDiagnostic> MappedDiagnostics
        => Snapshot.Diagnostics
            .Select(diagnostic => diagnostic with { Range = Mapping.Map(diagnostic.Range) })
            .ToArray();

    public IReadOnlyList<SymbolNode> MappedSymbols
        => Symbols?.Nodes
            .Select(symbol => symbol with
            {
                Uri = Mapping.ParentUri ?? Zone.ParentUri ?? symbol.Uri,
                Range = Mapping.Map(symbol.Range),
                SelectionRange = Mapping.Map(symbol.SelectionRange),
            })
            .ToArray()
            ?? [];
}
