using Dsh.Lsp.Protocol;

namespace Dsh.Lsp.Model;

public sealed record IndexedSymbol(
    string Id,
    string Name,
    string Kind,
    string? Detail,
    string Uri,
    LspRange Range,
    LspRange SelectionRange,
    string? ScopeId);

public sealed record IndexedReference(
    string FromSymbolId,
    SymbolEdgeKind Kind,
    string TargetUri,
    LspRange TargetRange,
    string SourceMethod);

public sealed record WorkspaceIndexSnapshot(
    string Root,
    IndexedSymbol[] Symbols,
    IndexedReference[] References);

public sealed record SymbolReferenceHit(
    IndexedSymbol Symbol,
    IndexedReference Reference,
    bool Outgoing);

/** In-memory cross-file symbol index. A loaded snapshot is read-only until the next AddOrUpdate. */
public sealed class WorkspaceIndex(string root)
{
    private readonly Dictionary<string, DocumentAnalysis> _documents = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private WorkspaceIndexSnapshot? _snapshot;

    public string Root { get; } = Path.GetFullPath(root);

    public IReadOnlyList<DocumentAnalysis> Documents
    {
        get
        {
            lock (_gate)
                return [.. _documents.Values];
        }
    }

    public void AddOrUpdate(DocumentAnalysis analysis)
    {
        lock (_gate)
        {
            _snapshot = null;
            _documents[analysis.Document.Uri] = analysis;
        }
    }

    public bool Remove(string uri)
    {
        lock (_gate)
        {
            _snapshot = null;
            return _documents.Remove(LspDocumentUri.Normalize(uri));
        }
    }

    public IReadOnlyList<IndexedSymbol> AllSymbols
    {
        get
        {
            lock (_gate)
            {
                return _documents.Values
                    .SelectMany(analysis => analysis.Symbols.Nodes.Select(symbol => Index(analysis, symbol)))
                    .Concat(_snapshot?.Symbols ?? [])
                    .ToArray();
            }
        }
    }

    public IReadOnlyList<IndexedReference> AllReferences
    {
        get
        {
            lock (_gate)
            {
                return _documents.Values
                    .SelectMany(analysis => analysis.Symbols.Edges.Select(edge => new IndexedReference(
                        edge.FromId,
                        edge.Kind,
                        edge.Target.Uri,
                        edge.Target.Range,
                        edge.SourceMethod)))
                    .Concat(_snapshot?.References ?? [])
                    .ToArray();
            }
        }
    }

    public IReadOnlyList<IndexedSymbol> FindSymbols(string query, int max = 100)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];
        var symbols = AllSymbols;
        var exact = symbols.Where(symbol => symbol.Name.Equals(query, StringComparison.OrdinalIgnoreCase));
        var prefix = symbols.Where(symbol => symbol.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase));
        var contains = symbols.Where(symbol => symbol.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        return exact.Concat(prefix).Concat(contains)
            .DistinctBy(symbol => symbol.Id, StringComparer.Ordinal)
            .Take(Math.Max(0, max))
            .ToArray();
    }

    public IndexedSymbol? FindSymbolAt(LspLocation location)
        => AllSymbols
            .Where(symbol => symbol.Uri == location.Uri && symbol.Range.Contains(location.Range))
            .OrderBy(symbol => symbol.Range.Start)
            .LastOrDefault();

    public IReadOnlyList<SymbolReferenceHit> FindReferences(string symbolId)
    {
        var symbol = AllSymbols.FirstOrDefault(candidate => candidate.Id == symbolId);
        if (symbol is null)
            return [];
        var references = AllReferences;
        var outgoing = references
            .Where(reference => reference.FromSymbolId == symbolId)
            .Select(reference => new SymbolReferenceHit(
                AllSymbols.FirstOrDefault(candidate => candidate.Id == reference.FromSymbolId) ?? symbol,
                reference,
                true));
        var incoming = references
            .Where(reference => reference.TargetUri == symbol.Uri && symbol.Range.Contains(reference.TargetRange))
            .Select(reference => new SymbolReferenceHit(
                AllSymbols.FirstOrDefault(candidate => candidate.Id == reference.FromSymbolId) ?? symbol,
                reference,
                false));
        return outgoing.Concat(incoming)
            .DistinctBy(hit => (hit.Reference.FromSymbolId, hit.Reference.TargetUri, hit.Reference.TargetRange, hit.Outgoing))
            .ToArray();
    }

    public WorkspaceIndexSnapshot Snapshot()
        => new(Root, [.. AllSymbols], [.. AllReferences]);

    public static WorkspaceIndex FromSnapshot(WorkspaceIndexSnapshot snapshot)
    {
        var index = new WorkspaceIndex(snapshot.Root) { _snapshot = snapshot };
        return index;
    }

    private static IndexedSymbol Index(DocumentAnalysis analysis, SymbolNode symbol)
        => new(
            symbol.Id,
            symbol.Name,
            symbol.Kind,
            symbol.Detail,
            symbol.Uri,
            symbol.Range,
            symbol.SelectionRange,
            analysis.Scopes.SymbolScopes.GetValueOrDefault(symbol.Id));
}
