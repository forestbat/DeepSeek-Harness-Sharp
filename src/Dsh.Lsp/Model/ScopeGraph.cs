using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Model;

public sealed record ScopeNode(
    string Id,
    string Kind,
    string? Name,
    LspRange Range,
    string? ParentId,
    IReadOnlyList<string> Declarations);

public sealed record ShadowingFact(
    string Name,
    string OuterSymbolId,
    string InnerSymbolId,
    string ScopeId);

public sealed record ScopeGraph(
    string Uri,
    int Version,
    IReadOnlyList<ScopeNode> Scopes,
    IReadOnlyDictionary<string, string> SymbolScopes,
    IReadOnlyList<ShadowingFact> Shadowing)
{
    public ScopeNode? FindScopeAt(LspPosition position)
    {
        ScopeNode? best = null;
        foreach (var scope in Scopes)
        {
            if (!scope.Range.Contains(position))
                continue;
            if (best is null
                || scope.Range.Start.CompareTo(best.Range.Start) > 0
                || (scope.Range.Start == best.Range.Start && scope.Range.End.CompareTo(best.Range.End) < 0))
                best = scope;
        }

        return best;
    }

    public IReadOnlyList<ScopeNode> Ancestors(string scopeId)
    {
        var byId = Scopes.ToDictionary(scope => scope.Id, StringComparer.Ordinal);
        var result = new List<ScopeNode>();
        var current = scopeId;
        while (byId.TryGetValue(current, out var scope) && scope.ParentId is { } parentId)
        {
            if (!byId.TryGetValue(parentId, out var parent))
                break;
            result.Add(parent);
            current = parentId;
        }

        return result;
    }

    public IReadOnlyList<ScopeNode> Children(string? parentId)
        => Scopes.Where(scope => scope.ParentId == parentId).ToArray();
}
