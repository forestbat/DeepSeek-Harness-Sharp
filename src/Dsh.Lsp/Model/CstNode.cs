using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Model;

/** Full concrete-syntax node supplied by an optional parser backend. */
public sealed record CstNode(
    string Type,
    string? FieldName,
    bool IsNamed,
    bool IsMissing,
    bool IsExtra,
    int StartByte,
    int EndByte,
    LspRange Range,
    IReadOnlyList<CstNode> Children)
{
    public bool IsError => Type == "ERROR" || IsMissing;

    public IEnumerable<CstNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var descendant in child.Descendants())
                yield return descendant;
        }
    }

    public CstNode? FindAt(LspPosition position)
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
