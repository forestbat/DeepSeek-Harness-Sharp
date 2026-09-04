using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordis.Node;

public sealed class JsUndefined
{
    public static readonly JsUndefined Instance = new();
    private JsUndefined() { }
    public override string ToString() => "undefined";
}

public sealed class JsHandle(NodeHost host, long id)
{
    public NodeHost Host { get; } = host;
    public long Id { get; } = id;

    public async Task<object?> Get(string prop)
    {
        return await Host.RequestAsync("hget", new JsonObject { ["id"] = Id, ["prop"] = prop });
    }

    public async Task<object?> Call(params object?[] args)
    {
        return await Host.RequestAsync("hcall", new JsonObject
        {
            ["id"] = Id,
            ["args"] = NodeMarshal.Marshal(Host, args),
        });
    }

    public override string ToString() => $"JsHandle({Id})";
}

public static class NodeMarshal
{
    public static JsonNode? Marshal(NodeHost host, object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsUndefined:
                return new JsonObject { ["$u"] = 1 };
            case JsHandle handle:
                return new JsonObject { ["$h"] = handle.Id };
            case JsPromise promise:
                return new JsonObject { ["$p"] = promise.Id };
            case Exception error:
                return new JsonObject { ["$e"] = error.Message, ["stack"] = error.ToString() };
            case JsonNode node:
                return node.DeepClone();
            case Task or ValueTask or ValueTask<object?>:
                return new JsonObject { ["$p"] = host.Promises.Track(value).Id };
            case bool b:
                return JsonValue.Create(b);
            case string s:
                return JsonValue.Create(s);
            case int or long or short or byte or double or float or decimal:
                return JsonValue.Create(value);
            case IDictionary<string, object?> dict:
                var obj = new JsonObject();
                foreach (var (key, item) in dict) obj[key] = Marshal(host, item);
                return obj;
            case System.Collections.IEnumerable list when value is not string:
                var array = new JsonArray();
                foreach (var item in list) array.Add(Marshal(host, item));
                return array;
            default:
                return new JsonObject
                {
                    ["$h"] = host.Handles.Store(value),
                    ["$kind"] = value switch
                    {
                        Context => "ctx",
                        Fiber => "fiber",
                        _ => null,
                    },
                };
        }
    }

    public static object? Unmarshal(NodeHost host, JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonValue value:
                return UnmarshalValue(value);
            case JsonArray array:
                return array.Select(item => Unmarshal(host, item)).ToList();
            case JsonObject obj:
                return UnmarshalObject(host, obj);
            default:
                return null;
        }
    }

    private static object? UnmarshalValue(JsonValue value)
    {
        if (value.TryGetValue<bool>(out var b)) return b;
        if (value.TryGetValue<string>(out var s)) return s;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<double>(out var d)) return d;
        return null;
    }

    private static object? UnmarshalObject(NodeHost host, JsonObject obj)
    {
        if (obj["$u"] is not null) return JsUndefined.Instance;
        if (obj["$bi"] is not null) return long.Parse(obj["$bi"]!.GetValue<string>());
        if (obj["$e"] is not null)
        {
            return new JsRemoteException(
                obj["$e"]!.GetValue<string>(),
                obj["stack"]?.GetValue<string>());
        }
        if (obj["$h"] is not null)
        {
            var id = obj["$h"]!.GetValue<long>();
            if (id < 0) return host.Handles.Resolve(id);
            return new JsHandle(host, id);
        }
        if (obj["$p"] is not null)
        {
            var id = obj["$p"]!.GetValue<long>();
            if (id < 0) return host.Promises.ResolveLocal(id);
            return host.Promises.TrackRemote(id);
        }
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in obj)
        {
            dict[key] = Unmarshal(host, value);
        }
        return dict;
    }

    public static object?[] UnmarshalArgs(NodeHost host, JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array.Select(item => Unmarshal(host, item)).ToArray();
        }
        return [];
    }
}

public sealed class JsPromise(NodeHost host, long id)
{
    public NodeHost Host { get; } = host;
    public long Id { get; } = id;
}
