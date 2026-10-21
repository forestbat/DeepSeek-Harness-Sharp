using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Model;

public sealed record SyntaxIdentityMatch(
    string Id,
    SyntaxNode LspNode,
    SyntaxNode? ParserNode);

public sealed record SyntaxNode(
    string Name,
    string? Detail,
    int SymbolKind,
    LspRange Range,
    LspRange SelectionRange,
    IReadOnlyList<string> Tags,
    IReadOnlyList<SyntaxNode> Children,
    string? Role = null,
    bool IsError = false,
    int DocumentVersion = 0,
    string? Provenance = null,
    string? ParentId = null,
    string? Id = null)
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
    CstNode? Cst = null,
    IReadOnlyList<SelectionChain>? SelectionChains = null,
    string SourceKind = "lsp",
    bool IsPartial = false,
    IReadOnlyList<SyntaxNode>? ParserRoots = null)
{
    public IReadOnlyList<SyntaxIdentityMatch> IdentityMatches
    {
        get
        {
            var parserNodes = (ParserRoots ?? [])
                .SelectMany(root => new[] { root }.Concat(root.Descendants()))
                .ToArray();
            return Nodes
                .Select(node => new SyntaxIdentityMatch(
                    node.Id ?? $"{node.Range.Start.Line}:{node.Range.Start.Character}|{node.Name}",
                    node,
                    FindParserMatch(node, parserNodes)))
                .ToArray();
        }
    }

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

    public IReadOnlyList<SyntaxNode> ErrorNodes
        => Diagnostics
            .Select(diagnostic => new SyntaxNode(
                diagnostic.Message,
                diagnostic.Code,
                0,
                diagnostic.Range,
                diagnostic.Range,
                [],
                [],
                Role: "diagnostic",
                IsError: true,
                DocumentVersion: Version,
                Provenance: "diagnostics"))
            .ToArray();

    public IReadOnlyList<CstNode> TriviaNodes
        => Cst is null
            ? Array.Empty<CstNode>()
            : new[] { Cst }.Concat(Cst.Descendants()).Where(node => node.IsExtra).ToArray();

    public IReadOnlyList<CstNode> CstErrorNodes
        => Cst is null
            ? Array.Empty<CstNode>()
            : new[] { Cst }.Concat(Cst.Descendants()).Where(node => node.IsError).ToArray();

    private static SyntaxNode? FindParserMatch(SyntaxNode lspNode, IReadOnlyList<SyntaxNode> parserNodes)
    {
        var candidates = parserNodes
            .Where(candidate => candidate.Name == lspNode.Name && candidate.Range.Overlaps(lspNode.Range))
            .ToArray();
        return candidates.FirstOrDefault(candidate => candidate.Range == lspNode.Range)
            ?? candidates
                .OrderBy(candidate => candidate.Range.End.CompareTo(candidate.Range.Start))
                .FirstOrDefault(candidate => candidate.Range.Contains(lspNode.Range)
                    || lspNode.Range.Contains(candidate.Range));
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
