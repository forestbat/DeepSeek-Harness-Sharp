using Dsh.Lsp.Client;
using Dsh.Lsp.Model;
using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Analysis;

/** Builds a lexical scope tree from the symbol hierarchy and records shadowing by name. */
public static class ScopeGraphBuilder
{
    private static readonly HashSet<string> ScopeKinds = new(StringComparer.Ordinal)
    {
        "file",
        "module",
        "namespace",
        "package",
        "class",
        "interface",
        "struct",
        "enum",
        "function",
        "method",
        "constructor",
        "object",
    };

    public static ScopeGraph Build(LspDocument document, SyntaxSnapshot snapshot)
    {
        var fileScope = new ScopeBuilder(
            $"{document.Uri}|file",
            "file",
            Path.GetFileName(document.Uri),
            new LspRange(LspPosition.Zero, document.PositionAt(document.Length)),
            null);
        var scopes = new List<ScopeBuilder> { fileScope };
        var symbolScopes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in snapshot.Roots)
            Visit(document, root, fileScope, scopes, symbolScopes);

        var converted = scopes.Select(scope => scope.ToRecord()).ToArray();
        var byId = converted.ToDictionary(scope => scope.Id, StringComparer.Ordinal);
        var symbols = snapshot.Nodes
            .GroupBy(node => SymbolIdentity.For(document.Uri, node), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var shadowing = new List<ShadowingFact>();
        foreach (var (symbolId, scopeId) in symbolScopes)
        {
            if (!symbols.TryGetValue(symbolId, out var symbol) || !byId.TryGetValue(scopeId, out var scope))
                continue;
            foreach (var ancestor in Ancestors(byId, scope))
            {
                var outer = ancestor.Declarations
                    .Select(id => symbols.GetValueOrDefault(id))
                    .FirstOrDefault(candidate => candidate is not null && candidate.Name == symbol.Name);
                if (outer is null)
                    continue;
                shadowing.Add(new ShadowingFact(
                    symbol.Name,
                    SymbolIdentity.For(document.Uri, outer),
                    symbolId,
                    scopeId));
                break;
            }
        }

        return new ScopeGraph(document.Uri, document.Version, converted, symbolScopes, shadowing);
    }

    private static void Visit(
        LspDocument document,
        SyntaxNode node,
        ScopeBuilder parentScope,
        List<ScopeBuilder> scopes,
        Dictionary<string, string> symbolScopes)
    {
        var symbolId = SymbolIdentity.For(document.Uri, node);
        var isScope = ScopeKinds.Contains(node.Kind);
        var activeScope = parentScope;
        if (isScope)
        {
            var scope = new ScopeBuilder(
                $"{document.Uri}|{node.SelectionRange.Start.Line}:{node.SelectionRange.Start.Character}|{node.Name}",
                node.Kind,
                node.Name,
                node.Range,
                parentScope.Id);
            scopes.Add(scope);
            activeScope = scope;
        }

        symbolScopes[symbolId] = isScope ? parentScope.Id : activeScope.Id;
        (isScope ? parentScope : activeScope).Declarations.Add(symbolId);
        foreach (var child in node.Children)
            Visit(document, child, activeScope, scopes, symbolScopes);
    }

    private static IReadOnlyList<ScopeNode> Ancestors(
        IReadOnlyDictionary<string, ScopeNode> byId,
        ScopeNode scope)
    {
        var result = new List<ScopeNode>();
        var current = scope;
        while (current.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent))
        {
            result.Add(parent);
            current = parent;
        }

        return result;
    }

    private sealed class ScopeBuilder(
        string id,
        string kind,
        string? name,
        LspRange range,
        string? parentId)
    {
        public string Id { get; } = id;
        public List<string> Declarations { get; } = [];

        public ScopeNode ToRecord()
            => new(Id, kind, name, range, parentId, Declarations);
    }
}
