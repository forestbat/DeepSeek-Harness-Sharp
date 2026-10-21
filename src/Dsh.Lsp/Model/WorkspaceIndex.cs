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
    string? ScopeId,
    int DocumentVersion = 0,
    string? Provenance = null);

public sealed record IndexedReference(
    string FromSymbolId,
    SymbolEdgeKind Kind,
    string TargetUri,
    LspRange TargetRange,
    string SourceMethod,
    string? SourceUri = null,
    int DocumentVersion = 0,
    string? Provenance = null,
    int TargetDocumentVersion = 0);

public sealed record WorkspaceIndexSnapshot(
    string Root,
    IndexedSymbol[] Symbols,
    IndexedReference[] References,
    IReadOnlyDictionary<string, int>? DocumentVersions = null,
    StubDeclaration[]? Stubs = null,
    IReadOnlyDictionary<string, SyntaxNode[]>? ParserRoots = null);

public sealed record SymbolReferenceHit(
    IndexedSymbol Symbol,
    IndexedReference Reference,
    bool Outgoing);

public interface IDeclarationIndex
{
    IReadOnlyList<IndexedSymbol> FindSymbols(string query, int max = 100);

    IndexedSymbol? FindSymbolAt(LspLocation location);
}

public sealed record IndexInvalidation(
    string Uri,
    IReadOnlyList<string> RemovedSymbols,
    IReadOnlyList<string> AddedSymbols,
    IReadOnlyList<string> AffectedUris);

/** In-memory cross-file symbol index. A loaded snapshot is read-only until the next AddOrUpdate. */
public sealed class WorkspaceIndex(string root) : IDeclarationIndex
{
    private readonly Dictionary<string, DocumentSnapshot> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IndexedSymbol[]> _symbolsByUri = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IndexedReference[]> _referencesByUri = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _dependents = new(StringComparer.Ordinal);
    private readonly StubIndex _stubs = new();
    private readonly Dictionary<string, SyntaxNode[]> _parserRoots = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private WorkspaceIndexSnapshot? _snapshot;

    public string Root { get; } = Path.GetFullPath(root);

    public IReadOnlyList<DocumentSnapshot> Documents
    {
        get
        {
            lock (_gate)
                return [.. _documents.Values];
        }
    }

    public StubIndex Stubs => _stubs;

    public IndexInvalidation AddOrUpdate(DocumentAnalysis analysis)
        => AddOrUpdate(
            new DocumentSnapshot(
                analysis.Document,
                analysis.Capabilities,
                analysis.Syntax,
                analysis.Diagnostics,
                analysis.Validation,
                analysis.Zones),
            analysis.Symbols,
            analysis.Scopes);

    public IndexInvalidation AddOrUpdate(
        DocumentSnapshot snapshot,
        SymbolGraph symbols,
        ScopeGraph? scopes = null,
        IReadOnlyList<StubDeclaration>? stubs = null)
    {
        lock (_gate)
        {
            var uri = LspDocumentUri.Normalize(snapshot.Document.Uri);
            var removed = _symbolsByUri.TryGetValue(uri, out var oldSymbols)
                ? oldSymbols.Select(symbol => symbol.Id).ToArray()
                : [];
            var added = symbols.Nodes
                .Select(symbol => Index(snapshot, scopes, symbol))
                .ToArray();
            var removedRanges = removed
                .Select(id => oldSymbols?.FirstOrDefault(symbol => symbol.Id == id))
                .Where(symbol => symbol is not null)
                .Select(symbol => symbol!)
                .ToArray();
            var affected = CollectDependents(uri);
            if (affected.Count == 0 && removedRanges.Length > 0)
            {
                affected = _referencesByUri
                    .Where(pair => pair.Key != uri && pair.Value.Any(reference =>
                        reference.TargetUri == uri
                        && removedRanges.Any(symbol => symbol.Range.Overlaps(reference.TargetRange))))
                    .Select(pair => pair.Key)
                    .ToArray();
            }

            if (_referencesByUri.TryGetValue(uri, out var oldReferences))
                RemoveReverseReferences(uri, oldReferences);
            var references = symbols.Edges
                .Select(edge => new IndexedReference(
                    edge.FromId,
                    edge.Kind,
                    edge.Target.Uri,
                    edge.Target.Range,
                    edge.SourceMethod,
                    uri,
                    snapshot.Document.Version,
                    edge.SourceMethod,
                    _documents.TryGetValue(LspDocumentUri.Normalize(edge.Target.Uri), out var target)
                        ? target.Document.Version
                        : 0))
                .ToArray();
            AddReverseReferences(uri, references);
            _snapshot = null;
            _documents[uri] = snapshot;
            _stubs.Add(uri, stubs ?? StubsFromSymbols(snapshot, symbols));
            if (snapshot.Syntax.ParserRoots is { Count: > 0 } parserRoots)
                _parserRoots[uri] = [.. parserRoots];
            else
                _parserRoots.Remove(uri);
            _symbolsByUri[uri] = added;
            _referencesByUri[uri] = references;
            return new IndexInvalidation(uri, removed, [.. added.Select(symbol => symbol.Id)], affected);
        }
    }

    public IndexInvalidation UpdateRange(
        DocumentSnapshot snapshot,
        SymbolGraph symbols,
        ScopeGraph? scopes,
        LspRange changedRange,
        IReadOnlyList<StubDeclaration>? stubs = null)
    {
        lock (_gate)
        {
            var uri = LspDocumentUri.Normalize(snapshot.Document.Uri);
            var oldSymbols = _symbolsByUri.TryGetValue(uri, out var current) ? current : [];
            var removedSymbols = oldSymbols.Where(symbol => symbol.Range.Overlaps(changedRange)).ToArray();
            var retained = oldSymbols
                .Where(symbol => !symbol.Range.Overlaps(changedRange))
                .Select(symbol => symbol with { DocumentVersion = snapshot.Document.Version })
                .ToArray();
            var addedSymbols = symbols.Nodes
                .Where(symbol => symbol.Range.Overlaps(changedRange))
                .Select(symbol => Index(snapshot, scopes, symbol))
                .ToArray();
            var affected = CollectDependents(uri);
            _symbolsByUri[uri] = [.. retained, .. addedSymbols];
            _documents[uri] = snapshot;
            _stubs.RemoveRange(uri, changedRange);
            if (stubs is { Count: > 0 })
                _stubs.Add(uri, [.. _stubs.ForUri(uri), .. stubs.Where(stub => stub.Range.Overlaps(changedRange))]);
            if (_referencesByUri.TryGetValue(uri, out var oldReferences))
                RemoveReverseReferences(uri, oldReferences);
            var references = symbols.Edges
                .Select(edge => new IndexedReference(
                    edge.FromId,
                    edge.Kind,
                    edge.Target.Uri,
                    edge.Target.Range,
                    edge.SourceMethod,
                    uri,
                    snapshot.Document.Version,
                    edge.SourceMethod,
                    _documents.TryGetValue(LspDocumentUri.Normalize(edge.Target.Uri), out var target)
                        ? target.Document.Version
                        : 0))
                .ToArray();
            AddReverseReferences(uri, references);
            _referencesByUri[uri] = references;
            if (snapshot.Syntax.ParserRoots is { Count: > 0 } parserRoots)
                _parserRoots[uri] = [.. parserRoots];
            _snapshot = null;
            return new IndexInvalidation(
                uri,
                [.. removedSymbols.Select(symbol => symbol.Id)],
                [.. addedSymbols.Select(symbol => symbol.Id)],
                affected);
        }
    }

    public IReadOnlyList<string> Invalidate(string uri)
    {
        lock (_gate)
        {
            var normalized = LspDocumentUri.Normalize(uri);
            if (!_symbolsByUri.TryGetValue(normalized, out var symbols))
                return [];
            var affected = CollectDependents(normalized);
            if (affected.Count == 0)
            {
                affected = _referencesByUri
                    .Where(pair => pair.Key != normalized && pair.Value.Any(reference =>
                        reference.TargetUri == normalized
                        && symbols.Any(symbol => symbol.Range.Overlaps(reference.TargetRange))))
                    .Select(pair => pair.Key)
                    .ToArray();
            }

            if (_referencesByUri.TryGetValue(normalized, out var references))
                RemoveReverseReferences(normalized, references);
            _symbolsByUri.Remove(normalized);
            _referencesByUri.Remove(normalized);
            _documents.Remove(normalized);
            _stubs.Remove(normalized);
            _parserRoots.Remove(normalized);
            _snapshot = null;
            return affected;
        }
    }

    public IReadOnlyList<string> InvalidateRange(string uri, LspRange range)
    {
        lock (_gate)
        {
            var normalized = LspDocumentUri.Normalize(uri);
            var symbols = _symbolsByUri.TryGetValue(normalized, out var current) ? current : [];
            var affected = _referencesByUri
                .Where(pair => pair.Key != normalized && pair.Value.Any(reference =>
                    reference.TargetUri == normalized
                    && symbols.Any(symbol => symbol.Range.Overlaps(range) && symbol.Range.Overlaps(reference.TargetRange))))
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var dependent in CollectDependents(normalized))
                affected.Add(dependent);
            var removedIds = symbols
                .Where(symbol => symbol.Range.Overlaps(range))
                .Select(symbol => symbol.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (removedIds.Count > 0)
            {
                _symbolsByUri[normalized] = symbols
                    .Where(symbol => !removedIds.Contains(symbol.Id))
                    .ToArray();
                _stubs.RemoveRange(normalized, range);
                if (_referencesByUri.TryGetValue(normalized, out var references))
                {
                    RemoveReverseReferences(normalized, references);
                    var remaining = references
                        .Where(reference => !removedIds.Contains(reference.FromSymbolId))
                        .ToArray();
                    _referencesByUri[normalized] = remaining;
                    AddReverseReferences(normalized, remaining);
                }
            }

            return [.. affected];
        }
    }

    public bool Remove(string uri)
    {
        lock (_gate)
        {
            _snapshot = null;
            var normalized = LspDocumentUri.Normalize(uri);
            if (_referencesByUri.TryGetValue(normalized, out var references))
                RemoveReverseReferences(normalized, references);
            _symbolsByUri.Remove(normalized);
            _referencesByUri.Remove(normalized);
            _stubs.Remove(normalized);
            _parserRoots.Remove(normalized);
            return _documents.Remove(normalized);
        }
    }

    public IReadOnlyList<IndexedSymbol> AllSymbols
    {
        get
        {
            lock (_gate)
            {
                return _symbolsByUri.Values
                    .SelectMany(symbols => symbols)
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
                return _referencesByUri.Values
                    .SelectMany(references => references)
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

    public IReadOnlyList<ValidationIssue> Validate()
    {
        lock (_gate)
        {
            var issues = new List<ValidationIssue>();
            foreach (var pair in _referencesByUri)
            {
                var sourceVersion = _documents.TryGetValue(pair.Key, out var source) ? source.Document.Version : 0;
                foreach (var reference in pair.Value)
                {
                    if (reference.DocumentVersion != 0 && reference.DocumentVersion != sourceVersion)
                    {
                        issues.Add(new ValidationIssue(
                            "reference-version",
                            $"reference source version {reference.DocumentVersion} != {sourceVersion}",
                            reference.TargetRange));
                    }

                    var targetUri = LspDocumentUri.Normalize(reference.TargetUri);
                    if (targetUri != pair.Key && !_symbolsByUri.ContainsKey(targetUri))
                    {
                        issues.Add(new ValidationIssue(
                            "reference-target",
                            $"reference target is not indexed: {reference.TargetUri}",
                            reference.TargetRange));
                    }

                    if (_documents.TryGetValue(targetUri, out var target)
                        && reference.TargetDocumentVersion != 0
                        && target.Document.Version > reference.TargetDocumentVersion)
                    {
                        issues.Add(new ValidationIssue(
                            "reference-target-version",
                            $"reference target version moved to {target.Document.Version}",
                            reference.TargetRange));
                    }
                }
            }

            foreach (var (uri, document) in _documents)
            {
                if (_symbolsByUri.TryGetValue(uri, out var symbols))
                {
                    foreach (var symbol in symbols.Where(symbol =>
                        symbol.DocumentVersion != 0 && symbol.DocumentVersion != document.Document.Version))
                    {
                        issues.Add(new ValidationIssue(
                            "symbol-version",
                            $"symbol '{symbol.Name}' version {symbol.DocumentVersion} != {document.Document.Version}",
                            symbol.Range));
                    }
                }

                foreach (var stub in _stubs.ForUri(uri).Where(stub =>
                    stub.DocumentVersion != 0 && stub.DocumentVersion != document.Document.Version))
                {
                    issues.Add(new ValidationIssue(
                        "stub-version",
                        $"stub '{stub.Name}' version {stub.DocumentVersion} != {document.Document.Version}",
                        stub.Range));
                }
            }

            return issues;
        }
    }

    public WorkspaceIndexSnapshot Snapshot()
    {
        var versions = _documents.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Document.Version,
            StringComparer.Ordinal);
        foreach (var (uri, version) in _snapshot?.DocumentVersions ?? new Dictionary<string, int>())
            versions.TryAdd(uri, version);
        return new(
            Root,
            [.. AllSymbols],
            [.. AllReferences],
            versions,
            [.. _stubs.Snapshot()],
            _parserRoots.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    public IReadOnlyList<string> Dependents(string uri, bool transitive = true)
    {
        lock (_gate)
            return CollectDependents(LspDocumentUri.Normalize(uri), transitive);
    }

    public static WorkspaceIndex FromSnapshot(WorkspaceIndexSnapshot snapshot)
    {
        var index = new WorkspaceIndex(snapshot.Root) { _snapshot = snapshot };
        index._stubs.Load(snapshot.Stubs ?? []);
        foreach (var (uri, roots) in snapshot.ParserRoots ?? new Dictionary<string, SyntaxNode[]>())
            index._parserRoots[uri] = roots;
        index.RebuildReverseReferences();
        return index;
    }

    private void RebuildReverseReferences()
    {
        foreach (var pair in _referencesByUri)
            AddReverseReferences(pair.Key, pair.Value);
        foreach (var reference in _snapshot?.References ?? [])
        {
            var sourceUri = reference.SourceUri ?? reference.FromSymbolId.Split('|')[0];
            if (!_dependents.TryGetValue(reference.TargetUri, out var sources))
            {
                sources = new HashSet<string>(StringComparer.Ordinal);
                _dependents[reference.TargetUri] = sources;
            }

            sources.Add(sourceUri);
        }
    }

    private void AddReverseReferences(string sourceUri, IReadOnlyList<IndexedReference> references)
    {
        foreach (var reference in references)
        {
            var targetUri = LspDocumentUri.Normalize(reference.TargetUri);
            if (!_dependents.TryGetValue(targetUri, out var sources))
            {
                sources = new HashSet<string>(StringComparer.Ordinal);
                _dependents[targetUri] = sources;
            }

            sources.Add(sourceUri);
        }
    }

    private void RemoveReverseReferences(string sourceUri, IReadOnlyList<IndexedReference> references)
    {
        foreach (var reference in references)
        {
            var targetUri = LspDocumentUri.Normalize(reference.TargetUri);
            if (!_dependents.TryGetValue(targetUri, out var sources))
                continue;
            sources.Remove(sourceUri);
            if (sources.Count == 0)
                _dependents.Remove(targetUri);
        }
    }

    private IReadOnlyList<string> CollectDependents(string uri, bool transitive = true)
    {
        if (!_dependents.TryGetValue(uri, out var direct))
            return [];
        if (!transitive)
            return direct.Where(candidate => candidate != uri).ToArray();
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(direct);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (current == uri || !result.Add(current))
                continue;
            if (!_dependents.TryGetValue(current, out var next))
                continue;
            foreach (var item in next)
                pending.Enqueue(item);
        }

        return [.. result];
    }

    private static IndexedSymbol Index(
        DocumentSnapshot snapshot,
        ScopeGraph? scopes,
        SymbolNode symbol)
        => new(
            symbol.Id,
            symbol.Name,
            symbol.Kind,
            symbol.Detail,
            symbol.Uri,
            symbol.Range,
            symbol.SelectionRange,
            scopes?.SymbolScopes.GetValueOrDefault(symbol.Id),
            snapshot.Document.Version,
            symbol.Provenance ?? (symbol.Uri == snapshot.Document.Uri ? "lsp" : null));

    private static IReadOnlyList<StubDeclaration> StubsFromSymbols(DocumentSnapshot snapshot, SymbolGraph symbols)
        => symbols.Nodes
            .Select(symbol => new StubDeclaration(
                symbol.Id,
                symbol.Name,
                symbol.Kind.ToString(),
                symbol.Uri,
                symbol.Range,
                symbol.SelectionRange,
                snapshot.Document.Version,
                symbol.Provenance ?? "lsp"))
            .ToArray();
}
