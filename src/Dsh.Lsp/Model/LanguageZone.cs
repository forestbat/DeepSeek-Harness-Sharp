using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Model;

/** A caller-defined region parsed by a second language server. */
public sealed record LanguageZone(
    string Id,
    string Path,
    string LanguageId,
    string Text,
    LspRange? ParentRange = null,
    string? ParentUri = null);

public sealed record ZoneAnalysis(
    LanguageZone Zone,
    DocumentAnalysis Analysis,
    ZoneMapping Mapping)
{
    public IReadOnlyList<LspDiagnostic> MappedDiagnostics
        => Analysis.Diagnostics
            .Select(diagnostic => diagnostic with
            {
                Range = Mapping.Map(diagnostic.Range),
                RelatedInformation = diagnostic.RelatedInformation?
                    .Select(item => item with
                    {
                        Location = item.Location with { Range = Mapping.Map(item.Location.Range) },
                    })
                    .ToArray(),
            })
            .ToArray();

    public IReadOnlyList<SyntaxNode> MappedRoots
        => Analysis.Syntax.Roots.Select(MapNode).ToArray();

    public IReadOnlyList<SymbolNode> MappedSymbols
        => Analysis.Symbols.Nodes
            .Select(symbol => symbol with
            {
                Id = $"{Zone.Id}|{symbol.Id}",
                Uri = Mapping.ParentUri ?? Zone.Path,
                Range = Mapping.Map(symbol.Range),
                SelectionRange = Mapping.Map(symbol.SelectionRange),
            })
            .ToArray();

    public IReadOnlyList<SymbolEdge> MappedEdges
        => Analysis.Symbols.Edges
            .Select(edge => edge with
            {
                FromId = $"{Zone.Id}|{edge.FromId}",
                Target = edge.Target with
                {
                    Uri = Mapping.ParentUri ?? Zone.Path,
                    Range = Mapping.Map(edge.Target.Range),
                },
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

    public IReadOnlyList<InlayHintFact> MappedHints
        => Analysis.Types.Hints
            .Select(hint => hint with { Range = Mapping.Map(hint.Range) })
            .ToArray();

    public IReadOnlyList<SignatureFact> MappedSignatures
        => Analysis.Types.Signatures
            .Select(signature => signature with
            {
                SymbolId = $"{Zone.Id}|{signature.SymbolId}",
                Range = Mapping.Map(signature.Range),
            })
            .ToArray();

    public IReadOnlyList<SemanticToken> MappedSemanticTokens
        => Analysis.Syntax.SemanticTokens
            .Select(token => token with { Range = Mapping.Map(token.Range) })
            .ToArray();

    public IReadOnlyList<FoldingRange> MappedFoldingRanges
        => Analysis.Syntax.FoldingRanges
            .Select(folding => folding with
            {
                StartLine = Mapping.ParentRange.Start.Line + folding.StartLine,
                EndLine = Mapping.ParentRange.Start.Line + folding.EndLine,
            })
            .ToArray();

    public ScopeGraph MappedScopes
        => Analysis.Scopes with
        {
            Uri = Mapping.ParentUri ?? Zone.Path,
            Scopes = Analysis.Scopes.Scopes
                .Select(scope => scope with
                {
                    Id = $"{Zone.Id}|{scope.Id}",
                    Range = Mapping.Map(scope.Range),
                    ParentId = scope.ParentId is { } parent ? $"{Zone.Id}|{parent}" : null,
                    Declarations = scope.Declarations.Select(id => $"{Zone.Id}|{id}").ToArray(),
                })
                .ToArray(),
            SymbolScopes = Analysis.Scopes.SymbolScopes
                .ToDictionary(
                    pair => $"{Zone.Id}|{pair.Key}",
                    pair => $"{Zone.Id}|{pair.Value}",
                    StringComparer.Ordinal),
            Shadowing = Analysis.Scopes.Shadowing
                .Select(shadowing => shadowing with
                {
                    OuterSymbolId = $"{Zone.Id}|{shadowing.OuterSymbolId}",
                    InnerSymbolId = $"{Zone.Id}|{shadowing.InnerSymbolId}",
                    ScopeId = $"{Zone.Id}|{shadowing.ScopeId}",
                })
                .ToArray(),
        };

    public HierarchyGraph MappedHierarchy
        => Analysis.Hierarchy with
        {
            Uri = Mapping.ParentUri ?? Zone.Path,
            Nodes = Analysis.Hierarchy.Nodes
                .Select(node => node with
                {
                    Id = $"{Zone.Id}|{node.Id}",
                    Uri = Mapping.ParentUri ?? Zone.Path,
                    Range = Mapping.Map(node.Range),
                    SelectionRange = Mapping.Map(node.SelectionRange),
                })
                .ToArray(),
            Edges = Analysis.Hierarchy.Edges
                .Select(edge => edge with
                {
                    FromId = $"{Zone.Id}|{edge.FromId}",
                    Target = edge.Target with
                    {
                        Id = $"{Zone.Id}|{edge.Target.Id}",
                        Uri = Mapping.ParentUri ?? Zone.Path,
                        Range = Mapping.Map(edge.Target.Range),
                        SelectionRange = Mapping.Map(edge.Target.SelectionRange),
                    },
                })
                .ToArray(),
        };

    private SyntaxNode MapNode(SyntaxNode node)
        => node with
        {
            Range = Mapping.Map(node.Range),
            SelectionRange = Mapping.Map(node.SelectionRange),
            Children = node.Children.Select(MapNode).ToArray(),
        };
}

public sealed record ZoneMapping(LspRange ParentRange, string? ParentUri = null)
{
    public LspPosition Map(LspPosition position)
        => position.Line == 0
            ? new LspPosition(ParentRange.Start.Line, ParentRange.Start.Character + position.Character)
            : new LspPosition(ParentRange.Start.Line + position.Line, position.Character);

    public LspRange Map(LspRange range) => new(Map(range.Start), Map(range.End));
}
