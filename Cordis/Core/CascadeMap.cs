namespace Cordis;

public sealed class CascadeMap
{
    private readonly Dictionary<string, object?> _own = new();

    public CascadeMap? Parent { get; set; }

    public object? this[string key]
    {
        get
        {
            for (var map = this; map is not null; map = map.Parent)
            {
                if (map._own.TryGetValue(key, out var value)) return value;
            }
            return null;
        }
        set => _own[key] = value;
    }

    public bool Has(string key)
    {
        for (var map = this; map is not null; map = map.Parent)
        {
            if (map._own.ContainsKey(key)) return true;
        }
        return false;
    }

    public bool HasOwn(string key) => _own.ContainsKey(key);

    public bool Delete(string key) => _own.Remove(key);

    public IEnumerable<KeyValuePair<string, object?>> OwnEntries => _own;

    public IEnumerable<string> OwnKeys => _own.Keys;

    public IEnumerable<CascadeMap> Chain()
    {
        for (var map = this; map is not null; map = map.Parent) yield return map;
    }

    public CascadeMap Derive() => new() { Parent = this };

    public void ReplaceWith(CascadeMap source, CascadeMap? parent)
    {
        _own.Clear();
        foreach (var (key, value) in source._own)
        {
            _own[key] = value;
        }
        Parent = parent;
    }
}
