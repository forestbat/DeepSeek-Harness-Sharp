using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Model;

public sealed record SymbolNode(
    string Id,
    string Name,
    string Kind,
    string? Detail,
    string Uri,
    LspRange Range,
    LspRange SelectionRange);

public enum SymbolEdgeKind
{
    Definition,
    Declaration,
    Reference,
    TypeDefinition,
    Implementation,
}

public sealed record SymbolEdge(
    string FromId,
    SymbolEdgeKind Kind,
    LspLocation Target,
    string SourceMethod);

public sealed record SymbolGraph(
    string Uri,
    int Version,
    IReadOnlyList<SymbolNode> Nodes,
    IReadOnlyList<SymbolEdge> Edges)
{
    public IEnumerable<SymbolEdge> EdgesFrom(string symbolId)
        => Edges.Where(edge => edge.FromId == symbolId);
}

public static class SymbolIdentity
{
    public static string For(string uri, SyntaxNode node)
        => $"{uri}|{node.SelectionRange.Start.Line}:{node.SelectionRange.Start.Character}|{node.Name}";
}
