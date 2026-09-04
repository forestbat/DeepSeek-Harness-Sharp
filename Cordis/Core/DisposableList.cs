using System.Text.Json.Nodes;

namespace Cordis;

public sealed class DisposableList<T> : IEnumerable<T> where T : class
{
    private int _sn;
    private readonly Dictionary<int, T> _map = new();
    private readonly Dictionary<T, int> _weak = new(ReferenceEqualityComparer.Instance);

    public int Length => _map.Count;

    public Func<bool> Push(T value)
    {
        var sn = ++_sn;
        _map[sn] = value;
        _weak[value] = sn;
        return () => _map.Remove(sn);
    }

    public Func<bool> Unshift(T value)
    {
        var sn = ++_sn;
        var rebuilt = new Dictionary<int, T> { [sn] = value };
        foreach (var (key, item) in _map) rebuilt[key] = item;
        _map.Clear();
        foreach (var (key, item) in rebuilt) _map[key] = item;
        _weak[value] = sn;
        return () => _map.Remove(sn);
    }

    public bool Delete(T value)
    {
        if (!_weak.TryGetValue(value, out var sn)) return false;
        _weak.Remove(value);
        return _map.Remove(sn);
    }

    public List<T> Clear()
    {
        var values = _map.Values.ToList();
        _map.Clear();
        _weak.Clear();
        values.Reverse();
        return values;
    }

    public IEnumerator<T> GetEnumerator() => _map.Values.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class CordisUtils
{
    public static object? GetOrNull(this IDictionary<string, object?> dict, string key)
    {
        return dict.TryGetValue(key, out var value) ? value : null;
    }

    public static string Hyphenate(string name)
    {
        var chars = new List<char>(name.Length + 4);
        foreach (var c in name)
        {
            if (char.IsUpper(c) && chars.Count > 0)
            {
                chars.Add('-');
                chars.Add(char.ToLowerInvariant(c));
            }
            else
            {
                chars.Add(char.ToLowerInvariant(c));
            }
        }
        return new string(chars.ToArray());
    }

    public static bool DeepEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a is JsonNode ja && b is JsonNode jb) return JsonNode.DeepEquals(ja, jb);
        if (a is IDictionary<string, object?> da && b is IDictionary<string, object?> db)
        {
            if (da.Count != db.Count) return false;
            foreach (var (key, value) in da)
            {
                if (!db.TryGetValue(key, out var other) || !DeepEqual(value, other)) return false;
            }
            return true;
        }
        if (a is IEnumerable<object?> ea && b is IEnumerable<object?> eb && a is not string && b is not string)
        {
            return ea.SequenceEqual(eb, new DeepEqualComparer());
        }
        return Equals(a, b);
    }

    private sealed class DeepEqualComparer : IEqualityComparer<object?>
    {
        public new bool Equals(object? x, object? y) => DeepEqual(x, y);
        public int GetHashCode(object? obj) => obj?.GetHashCode() ?? 0;
    }
}
