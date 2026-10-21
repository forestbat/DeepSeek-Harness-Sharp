using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Model;

public sealed record HierarchyNode(
    string Id,
    string Name,
    string Kind,
    string Uri,
    LspRange Range,
    LspRange SelectionRange);

public enum HierarchyEdgeKind
{
    IncomingCall,
    OutgoingCall,
    Supertype,
    Subtype,
}

public sealed record HierarchyEdge(
    string FromId,
    HierarchyEdgeKind Kind,
    HierarchyNode Target);

public sealed record HierarchyGraph(
    string Uri,
    int Version,
    IReadOnlyList<HierarchyNode> Nodes,
    IReadOnlyList<HierarchyEdge> Edges)
{
    public static HierarchyGraph Empty(string uri, int version) => new(uri, version, [], []);
}
