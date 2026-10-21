using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Model;

public sealed record TypeFact(
    string SymbolId,
    string Display,
    string SourceMethod,
    LspRange? Range,
    string? RawMarkdown,
    string? Format = null,
    string? Language = null,
    TypeExpression? Expression = null,
    int DocumentVersion = 0);

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
    LspRange Range,
    IReadOnlyList<SignatureParameterFact>? Parameters = null,
    TypeExpression? ReturnType = null,
    int DocumentVersion = 0);

public sealed record SignatureParameterFact(
    string Label,
    string? Documentation,
    TypeExpression? Type = null);

public sealed record TypeFactsSnapshot(
    IReadOnlyList<TypeFact> Facts,
    IReadOnlyList<InlayHintFact> Hints,
    IReadOnlyList<SignatureFact> Signatures)
{
    public static TypeFactsSnapshot Empty { get; } = new([], [], []);
}
