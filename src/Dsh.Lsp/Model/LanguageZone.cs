using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Model;

/** A caller-defined region parsed by a second language server. */
public sealed record LanguageZone(
    string Id,
    string Path,
    string LanguageId,
    string Text,
    LspRange? ParentRange = null);

public sealed record ZoneAnalysis(
    LanguageZone Zone,
    DocumentAnalysis Analysis,
    ZoneMapping Mapping)
{
    public IReadOnlyList<LspDiagnostic> MappedDiagnostics
        => Analysis.Diagnostics
            .Select(diagnostic => diagnostic with { Range = Mapping.Map(diagnostic.Range) })
            .ToArray();

    public IReadOnlyList<SyntaxNode> MappedRoots
        => Analysis.Syntax.Roots.Select(MapNode).ToArray();

    public IReadOnlyList<SymbolNode> MappedSymbols
        => Analysis.Symbols.Nodes
            .Select(symbol => symbol with
            {
                Id = $"{Zone.Id}|{symbol.Id}",
                Uri = Zone.Path,
                Range = Mapping.Map(symbol.Range),
                SelectionRange = Mapping.Map(symbol.SelectionRange),
            })
            .ToArray();

    public IReadOnlyList<TypeFact> MappedTypes
        => Analysis.Types.Facts
            .Select(fact => fact with
            {
                SymbolId = $"{Zone.Id}|{fact.SymbolId}",
                Range = fact.Range is { } range ? Mapping.Map(range) : null,
            })
            .ToArray();

    private SyntaxNode MapNode(SyntaxNode node)
        => node with
        {
            Range = Mapping.Map(node.Range),
            SelectionRange = Mapping.Map(node.SelectionRange),
            Children = node.Children.Select(MapNode).ToArray(),
        };
}

public sealed record ZoneMapping(LspRange ParentRange)
{
    public LspPosition Map(LspPosition position)
        => position.Line == 0
            ? new LspPosition(ParentRange.Start.Line, ParentRange.Start.Character + position.Character)
            : new LspPosition(ParentRange.Start.Line + position.Line, position.Character);

    public LspRange Map(LspRange range) => new(Map(range.Start), Map(range.End));
}
