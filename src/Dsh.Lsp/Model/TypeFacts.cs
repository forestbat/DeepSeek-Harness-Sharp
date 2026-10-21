using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Model;

public sealed record TypeFact(
    string SymbolId,
    string Display,
    string SourceMethod,
    LspRange? Range,
    string? RawMarkdown);

public sealed record InlayHintFact(
    LspRange Range,
    string Label,
    bool PaddingLeft,
    bool PaddingRight,
    string? Tooltip);

public sealed record SignatureFact(
    string SymbolId,
    string Label,
    string? Documentation,
    int? ActiveParameter,
    LspRange Range);

public sealed record TypeFactsSnapshot(
    IReadOnlyList<TypeFact> Facts,
    IReadOnlyList<InlayHintFact> Hints,
    IReadOnlyList<SignatureFact> Signatures)
{
    public static TypeFactsSnapshot Empty { get; } = new([], [], []);
}
