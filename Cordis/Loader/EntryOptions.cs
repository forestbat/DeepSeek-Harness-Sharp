namespace Cordis.Loader;

public sealed record JsExpr(string Expr);

public static class ConfigValues
{
    public static bool IsJsExpr(object? value) => value is JsExpr;

    public static bool IsNull(object? value) => value is null;
}

public sealed class EntryOptions
{
    private static readonly string[] PrependKeys = ["id", "name"];
    private static readonly string[] AppendKeys = ["config"];

    private readonly Dictionary<string, object?> _data = new();

    public object? this[string key]
    {
        get => _data.GetValueOrDefault(key);
        set
        {
            if (value is null) _data.Remove(key);
            else _data[key] = value;
        }
    }

    public string? Id
    {
        get => this["id"] as string;
        set => this["id"] = value;
    }

    public string? Name
    {
        get => this["name"] as string;
        set => this["name"] = value;
    }

    public object? Config
    {
        get => this["config"];
        set => this["config"] = value;
    }

    public bool Group
    {
        get => this["group"] as bool? ?? false;
        set => this["group"] = value;
    }

    public object? Disabled
    {
        get => this["disabled"];
        set => this["disabled"] = value;
    }

    public Dictionary<string, object?>? Inject
    {
        get => this["inject"] as Dictionary<string, object?>;
        set => this["inject"] = value;
    }

    public Dictionary<string, object?>? Intercept
    {
        get => this["intercept"] as Dictionary<string, object?>;
        set => this["intercept"] = value;
    }

    public Dictionary<string, object?>? Isolate
    {
        get => this["isolate"] as Dictionary<string, object?>;
        set => this["isolate"] = value;
    }

    public IEnumerable<string> Keys => _data.Keys;

    public bool ContainsKey(string key) => _data.ContainsKey(key);

    public EntryOptions Clone()
    {
        var clone = new EntryOptions();
        foreach (var (key, value) in _data) clone._data[key] = value;
        return clone;
    }

    public void SortKeys()
    {
        var prepend = Take(PrependKeys);
        var append = Take(AppendKeys);
        var rest = Take(_data.Keys.ToArray());
        Array.Sort(rest, (a, b) => string.CompareOrdinal(a.Key, b.Key));
        foreach (var (key, value) in prepend.Concat(rest).Concat(append))
        {
            _data[key] = value;
        }
    }

    private KeyValuePair<string, object?>[] Take(IEnumerable<string> keys)
    {
        var result = new List<KeyValuePair<string, object?>>();
        foreach (var key in keys)
        {
            if (!_data.TryGetValue(key, out var value)) continue;
            result.Add(new KeyValuePair<string, object?>(key, value));
            _data.Remove(key);
        }
        return result.ToArray();
    }

    public static EntryOptions From(object? value)
    {
        if (value is EntryOptions options) return options;
        var result = new EntryOptions();
        if (value is IDictionary<string, object?> dict)
        {
            foreach (var (key, item) in dict) result._data[key] = item;
        }
        return result;
    }
}
