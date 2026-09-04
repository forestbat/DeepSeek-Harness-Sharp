using Cordis;

namespace Dsh.Core;

public sealed class ScopeKey
{
    private ScopeKey? _parent;

    public ScopeKey? Parent
    {
        get => _parent;
        internal set
        {
            if (value is not null && value.ScopeChain().Contains(this))
                throw new InvalidOperationException("scope parent binding would create a cycle");
            _parent = value;
        }
    }

    public IEnumerable<ScopeKey> ScopeChain()
    {
        for (var key = this; key is not null; key = key._parent)
            yield return key;
    }
}

public static class DshScope
{
    private static readonly object ScopeTag = new();

    public static Context CreateScope(Context ctx, ScopeKey key)
        => ctx.Extend((ScopeTag, key));

    public static ScopeKey? ScopeOf(Context ctx)
        => ctx.GetProp(ScopeTag) as ScopeKey;

    public static void BindScopeParent(ScopeKey key, ScopeKey parent)
        => key.Parent = parent;

    public static IReadOnlyList<ScopeKey> ScopeChainOf(ScopeKey key)
        => key.ScopeChain().ToList();

    public static bool IsInScope(Context ctx, ScopeKey key)
    {
        var tag = ScopeOf(ctx);
        return tag is not null && tag.ScopeChain().Contains(key);
    }

    public static Context ScopeTarget(Context ctx, ScopeKey? scope)
    {
        if (scope is null)
            return ctx;
        return ctx.Extend((Cordis.Symbols.Filter, (Func<Context, bool>)(hookCtx => IsInScope(hookCtx, scope))));
    }
}

public sealed class NamedEntries<T>
{
    private readonly Dictionary<string, T> _entries = [];
    private readonly Func<string, Exception> _conflict;

    public NamedEntries(Func<string, Exception> conflict)
    {
        _conflict = conflict;
    }

    public void Insert(string name, T value)
    {
        if (_entries.ContainsKey(name))
            throw _conflict(name);
        _entries[name] = value;
    }

    public bool Remove(string name) => _entries.Remove(name);

    public IEnumerable<KeyValuePair<string, T>> Entries => _entries;

    public bool IsEmpty => _entries.Count == 0;
}

public sealed class AnonymousEntries<T>
{
    private readonly List<T> _entries = [];

    public void Append(T value) => _entries.Add(value);

    public bool Remove(T value) => _entries.Remove(value);

    public IReadOnlyList<T> Values => _entries;

    public bool IsEmpty => _entries.Count == 0;
}

public sealed class ScopedLayers<TLayer>
{
    private readonly Func<ScopeKey?, TLayer> _factory;
    private readonly Action _onChanged;
    private readonly Dictionary<ScopeKey, TLayer> _scoped = [];

    public ScopedLayers(Func<ScopeKey?, TLayer> factory, Action onChanged)
    {
        _factory = factory;
        _onChanged = onChanged;
        Global = factory(null);
    }

    public TLayer Global { get; }

    public TLayer LayerFor(ScopeKey? scope)
    {
        if (scope is null)
            return Global;
        if (!_scoped.TryGetValue(scope, out var layer))
            _scoped[scope] = layer = _factory(scope);
        return layer;
    }

    public IReadOnlyList<TLayer> ChainLayers(ScopeKey? scope)
    {
        if (scope is null)
            return [];
        return scope.ScopeChain().Reverse().Select(LayerFor).ToList();
    }

    public Dictionary<string, TEntry> Merge<TEntry>(ScopeKey? scope, Func<TLayer, NamedEntries<TEntry>> select)
    {
        var merged = new Dictionary<string, TEntry>();
        foreach (var (name, entry) in select(Global).Entries)
            merged[name] = entry;
        foreach (var layer in ChainLayers(scope))
        {
            foreach (var (name, entry) in select(layer).Entries)
                merged[name] = entry;
        }
        return merged;
    }

    public IDisposable Effect(Context ctx, ScopeKey? scope, Action<TLayer> register, Action<TLayer> unregister, bool notify = true)
    {
        var layer = LayerFor(scope ?? DshScope.ScopeOf(ctx));
        register(layer);
        if (notify)
            _onChanged();
        return new ScopeEffect(() =>
        {
            unregister(layer);
            if (notify)
                _onChanged();
        });
    }

    private sealed class ScopeEffect(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            dispose();
        }
    }
}
