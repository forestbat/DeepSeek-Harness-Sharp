namespace Dsh.Lsp.Model;

public sealed record StubDeclaration(
    string Id,
    string Name,
    string Kind,
    string Uri,
    Dsh.Lsp.Protocol.LspRange Range,
    Dsh.Lsp.Protocol.LspRange SelectionRange,
    int DocumentVersion,
    string Source,
    string? ContainerId = null,
    bool IsPartial = false,
    string? Visibility = null,
    IReadOnlyList<string>? GenericParameters = null,
    bool IsGenerated = false);

public sealed class StubIndex
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, StubDeclaration[]> _byUri = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<StubDeclaration>> _byName = new(StringComparer.Ordinal);

    public void Add(string uri, IReadOnlyList<StubDeclaration> declarations)
    {
        lock (_gate)
        {
            RemoveCore(uri);
            _byUri[uri] = [.. declarations];
            foreach (var declaration in declarations)
            {
                if (!_byName.TryGetValue(declaration.Name, out var list))
                {
                    list = [];
                    _byName[declaration.Name] = list;
                }

                list.Add(declaration);
            }
        }
    }

    public void Remove(string uri)
    {
        lock (_gate)
            RemoveCore(uri);
    }

    public void RemoveRange(string uri, Dsh.Lsp.Protocol.LspRange range)
    {
        lock (_gate)
        {
            if (!_byUri.TryGetValue(uri, out var declarations))
                return;
            var remaining = declarations.Where(declaration => !declaration.Range.Overlaps(range)).ToArray();
            if (remaining.Length == declarations.Length)
                return;
            RemoveCore(uri);
            AddCore(uri, remaining);
        }
    }

    public IReadOnlyList<StubDeclaration> Find(string query, int max = 100)
    {
        lock (_gate)
        {
            return _byName
                .Where(pair => pair.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
                .SelectMany(pair => pair.Value)
                .Take(Math.Max(0, max))
                .ToArray();
        }
    }

    public IReadOnlyList<StubDeclaration> ForUri(string uri)
    {
        lock (_gate)
            return _byUri.TryGetValue(uri, out var declarations) ? [.. declarations] : [];
    }

    public IReadOnlyList<StubDeclaration> Snapshot()
    {
        lock (_gate)
            return [.. _byUri.Values.SelectMany(declarations => declarations)];
    }

    public void Load(IReadOnlyList<StubDeclaration> declarations)
    {
        lock (_gate)
        {
            _byUri.Clear();
            _byName.Clear();
            foreach (var group in declarations.GroupBy(declaration => declaration.Uri, StringComparer.Ordinal))
                AddCore(group.Key, group.ToArray());
        }
    }

    private void AddCore(string uri, IReadOnlyList<StubDeclaration> declarations)
    {
        _byUri[uri] = [.. declarations];
        foreach (var declaration in declarations)
        {
            if (!_byName.TryGetValue(declaration.Name, out var list))
            {
                list = [];
                _byName[declaration.Name] = list;
            }

            list.Add(declaration);
        }
    }

    private void RemoveCore(string uri)
    {
        if (!_byUri.Remove(uri, out var declarations))
            return;
        foreach (var declaration in declarations)
        {
            if (!_byName.TryGetValue(declaration.Name, out var list))
                continue;
            list.Remove(declaration);
            if (list.Count == 0)
                _byName.Remove(declaration.Name);
        }
    }
}
