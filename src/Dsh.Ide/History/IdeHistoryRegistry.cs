namespace Dsh.Ide.History;

public sealed class IdeHistoryRegistry
{
    private readonly List<IIdeHistoryProvider> _providers;

    public IdeHistoryRegistry(IEnumerable<IIdeHistoryProvider> providers)
    {
        _providers = [.. providers];
    }

    public static IdeHistoryRegistry CreateDefault(string? home = null, string? jetBrainsCacheRoot = null, IdeHistoryOptions? options = null)
    {
        var discovery = (options ?? IdeHistoryOptions.From(null)).Discovery;
        return new([
            new VsCodeLocalHistoryProvider(home, discovery.GetValueOrDefault("vscode") ?? new()),
            new JetBrainsLocalHistoryProvider(jetBrainsCacheRoot, discovery.GetValueOrDefault("jetbrains") ?? new()),
        ]);
    }

    public IReadOnlyList<(IIdeHistoryProvider Provider, IdeHistoryStoreInfo Store)> Discover()
        => _providers
            .SelectMany(provider => provider.Discover().Select(store => (provider, store)))
            .ToList();

    public IReadOnlyList<(IIdeHistoryProvider Provider, IdeHistoryStoreInfo Store, IdeHistoryEntry Entry)> Query(
        string? pathFilter, string? providerFilter, int limit, Func<string, bool>? enabled = null, long? from = null, long? to = null)
    {
        var results = new List<(IIdeHistoryProvider, IdeHistoryStoreInfo, IdeHistoryEntry)>();
        foreach (var provider in _providers)
        {
            if (enabled is not null && !enabled(provider.Name)
                || providerFilter is { Length: > 0 } && !provider.Name.Contains(providerFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var store in provider.Discover())
            {
                foreach (var entry in provider.List(store, pathFilter, from is not null || to is not null ? int.MaxValue : limit))
                    if ((from is null || entry.Timestamp >= from) && (to is null || entry.Timestamp <= to))
                        results.Add((provider, store, entry with { StoreLocation = store.Location }));
            }
        }
        return [.. results.OrderByDescending(item => item.Item3.Timestamp).Take(limit)];
    }

    public (IIdeHistoryProvider Provider, IdeHistoryStoreInfo Store, IdeHistoryEntry Entry)? Find(string path, long timestamp, string? providerFilter)
    {
        var matches = Query(path, providerFilter, 200, from: timestamp, to: timestamp)
            .Where(item => string.Equals(IdeHistoryPathFilter.Normalize(item.Entry.Path), IdeHistoryPathFilter.Normalize(path),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .ToList();
        foreach (var item in matches.Where(item => item.Provider.SupportsContent))
            return item;
        return matches.Count > 0 ? matches[0] : null;
    }

    public IdeHistoryContent? Read(IdeHistoryEntry entry)
    {
        if (entry.StoreLocation is null)
        {
            var match = Find(entry.Path, entry.Timestamp, entry.Provider);
            return match is { } found ? found.Provider.Read(found.Store, found.Entry) : null;
        }
        var provider = _providers.FirstOrDefault(candidate => candidate.Name == entry.Provider);
        var store = provider?.Discover().FirstOrDefault(candidate => candidate.Location == entry.StoreLocation);
        return store is null ? null : provider!.Read(store, entry);
    }

    public IReadOnlyList<string> RetentionNotes()
        => [.. Discover().Select(item => $"{item.Store.Provider}: {item.Store.Retention} @ {item.Store.Location}")];
}
