using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Model;

public sealed record SyntaxNode(
    string Name,
    string? Detail,
    int SymbolKind,
    LspRange Range,
    LspRange SelectionRange,
    IReadOnlyList<string> Tags,
    IReadOnlyList<SyntaxNode> Children)
{
    public string Kind => LspSymbolKinds.Name(SymbolKind);

    public IEnumerable<SyntaxNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var descendant in child.Descendants())
                yield return descendant;
        }
    }

    public SyntaxNode? FindAt(LspPosition position)
    {
        if (!Range.Contains(position))
            return null;
        foreach (var child in Children)
        {
            if (child.FindAt(position) is { } match)
                return match;
        }

        return this;
    }
}

public sealed record SemanticToken(LspRange Range, string Type, IReadOnlyList<string> Modifiers);

public sealed record FoldingRange(
    int StartLine,
    int? StartCharacter,
    int EndLine,
    int? EndCharacter,
    string? Kind);

public sealed record SyntaxSnapshot(
    string Uri,
    int Version,
    string LanguageId,
    IReadOnlyList<SyntaxNode> Roots,
    IReadOnlyList<SemanticToken> SemanticTokens,
    IReadOnlyList<FoldingRange> FoldingRanges,
    IReadOnlyList<LspDiagnostic> Diagnostics,
    CstNode? Cst = null)
{
    public IReadOnlyList<SyntaxNode> Nodes
    {
        get
        {
            var nodes = new List<SyntaxNode>();
            foreach (var root in Roots)
            {
                nodes.Add(root);
                nodes.AddRange(root.Descendants());
            }

            return nodes;
        }
    }

    public SyntaxNode? FindAt(LspPosition position)
    {
        SyntaxNode? best = null;
        foreach (var root in Roots)
        {
            if (root.FindAt(position) is not { } match)
                continue;
            if (best is null
                || match.Range.Start.CompareTo(best.Range.Start) > 0
                || (match.Range.Start == best.Range.Start && match.Range.End.CompareTo(best.Range.End) < 0))
                best = match;
        }

        return best;
    }
}

public static class LspSymbolKinds
{
    private static readonly string[] Names =
    [
        "unknown",
        "file",
        "module",
        "namespace",
        "package",
        "class",
        "method",
        "property",
        "field",
        "constructor",
        "enum",
        "interface",
        "function",
        "variable",
        "constant",
        "string",
        "number",
        "boolean",
        "array",
        "object",
        "key",
        "null",
        "enum-member",
        "struct",
        "event",
        "operator",
        "type-parameter",
    ];

    public static string Name(int kind) => kind >= 1 && kind < Names.Length ? Names[kind] : Names[0];
}
