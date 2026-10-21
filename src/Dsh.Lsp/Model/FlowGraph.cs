namespace Dsh.Lsp.Model;

public sealed record FlowOperation(string Kind, Dsh.Lsp.Protocol.LspRange Range, bool IsImplicit);

public sealed record FlowVariable(
    string Name,
    IReadOnlyList<Dsh.Lsp.Protocol.LspRange> Definitions,
    IReadOnlyList<Dsh.Lsp.Protocol.LspRange> Uses);

public sealed record FlowNode(
    int Id,
    string Kind,
    IReadOnlyList<FlowOperation>? Operations = null);

public sealed record FlowEdge(int From, int To, string Kind);

public sealed record FlowGraph(
    int? Entry,
    int? Exit,
    IReadOnlyList<FlowNode> Nodes,
    IReadOnlyList<FlowEdge> Edges,
    IReadOnlyList<FlowVariable>? Variables = null,
    IReadOnlyList<FlowEdge>? ExceptionEdges = null);
