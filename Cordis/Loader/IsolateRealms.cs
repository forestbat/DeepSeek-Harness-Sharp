namespace Cordis.Loader;

public abstract class Realm
{
    private readonly Dictionary<string, CordisSymbol> _store = new();

    protected abstract string Suffix { get; }

    public CordisSymbol Access(string key, bool create = false)
    {
        if (create)
        {
            if (!_store.TryGetValue(key, out var symbol))
            {
                symbol = _store[key] = CordisSymbol.New($"{key}{Suffix}");
            }
            return symbol;
        }
        return _store.TryGetValue(key, out var existing) ? existing : CordisSymbol.New($"{key}{Suffix}");
    }

    public void Delete(string key) => _store.Remove(key);

    public int Size => _store.Count;
}

public sealed class LocalRealm(Entry entry) : Realm
{
    protected override string Suffix => $"#{entry.Options.Id}";
}

public sealed class GlobalRealm(string label) : Realm
{
    public string Label { get; } = label;
    protected override string Suffix => $"@{Label}";
}

public static class IsolatePlugin
{
    public static PluginDefinition Definition { get; } = PluginDefinition.From(Apply, "isolate");

    private static object? Apply(Context ctx, object? config)
    {
        var realms = new Dictionary<string, GlobalRealm>();
        var delims = new Dictionary<string, CordisSymbol>();

        CordisSymbol? Access(Entry entry, string name, bool create)
        {
            Realm? realm;
            var label = entry.Options.Isolate?.GetOrNull(name);
            if (label is null) return null;
            if (label is true)
            {
                realm = entry.Realm ??= new LocalRealm(entry);
            }
            else if (label is string text)
            {
                if (create)
                {
                    if (!realms.TryGetValue(text, out var global))
                    {
                        global = realms[text] = new GlobalRealm(text);
                    }
                    realm = global;
                }
                else
                {
                    realm = realms.TryGetValue(text, out var global) ? global : null;
                }
            }
            else
            {
                return null;
            }
            return realm?.Access(name, create);
        }

        ctx.On("loader/entry-init", (thisArg, args) =>
        {
            var entry = (Entry)args[0]!;
            entry.Ctx.SetOwn(Symbols.Intercept, entry.Ctx.InterceptMap.Derive());
            entry.Ctx.SetOwn(Symbols.Isolate, entry.Ctx.IsolateMap.Derive());
            return new ValueTask<object?>();
        });

        ctx.On("loader/patch-context", (thisArg, args) =>
        {
            var entry = (Entry)args[0]!;
            var next = (Func<ValueTask<object?>>)args[1]!;
            return PatchContext(entry, next, Access, delims, ctx);
        });

        ctx.On("loader/partial-dispose", (thisArg, args) =>
        {
            var entry = (Entry)args[0]!;
            var legacy = (EntryOptions)args[1]!;
            var active = args[2] is true;
            CollectRealms(entry, legacy, active, realms, ctx);
            return new ValueTask<object?>();
        });

        return null;
    }

    private static async ValueTask<object?> PatchContext(
        Entry entry,
        Func<ValueTask<object?>> next,
        Func<Entry, string, bool, CordisSymbol?> access,
        Dictionary<string, CordisSymbol> delims,
        Context ctx)
    {
        var newMap = entry.Parent.Ctx.IsolateMap.Derive();
        foreach (var name in entry.Options.Isolate?.Keys ?? Enumerable.Empty<string>())
        {
            newMap[name] = access(entry, name, true);
        }

        var diff = new Dictionary<string, (CordisSymbol? Old, CordisSymbol? New, CordisSymbol Flag1, CordisSymbol Flag2)>();
        var oldMap = entry.Ctx.IsolateMap;
        var names = newMap.OwnKeys
            .Concat(delims.Keys)
            .Distinct();
        foreach (var name in names)
        {
            var oldSymbol = oldMap[name] as CordisSymbol;
            var newSymbol = newMap[name] as CordisSymbol;
            if (Equals(newSymbol, oldSymbol)) continue;
            if (!delims.TryGetValue(name, out var delim))
            {
                delim = delims[name] = CordisSymbol.New($"delim:{name}");
            }
            entry.Ctx.SetOwn(delim, CordisSymbol.New($"{name}#{entry.Id}"));
            foreach (var symbol in new[] { oldSymbol, newSymbol })
            {
                if (symbol is null) continue;
                if (!ctx.Reflect.Store.TryGetValue(symbol, out var impl)) continue;
                var flag1 = (CordisSymbol)entry.Ctx.GetProp(delim)!;
                var flag2 = (CordisSymbol?)impl.Fiber.Ctx.GetProp(delim) ?? CordisSymbol.New("unset");
                diff[name] = (oldSymbol, newSymbol, flag1, flag2);
                if (!Equals(flag1, flag2)) break;
            }
        }

        var parentIsolate = entry.Parent.Ctx.IsolateMap;
        var parentIntercept = entry.Parent.Ctx.InterceptMap;
        entry.Ctx.IsolateMap.ReplaceWith(newMap, parentIsolate);
        entry.Ctx.InterceptMap.ReplaceWith(ToCascade(entry.Options.Intercept), parentIntercept);

        await next();

        foreach (var (oldSymbol, newSymbol, flag1, flag2) in diff.Values)
        {
            if (oldSymbol is null || newSymbol is null) continue;
            if (Equals(flag1, flag2)
                && ctx.Reflect.Store.ContainsKey(oldSymbol)
                && !ctx.Reflect.Store.ContainsKey(newSymbol))
            {
                ctx.Reflect.Store[newSymbol] = ctx.Reflect.Store[oldSymbol];
                ctx.Reflect.Store.Remove(oldSymbol);
            }
        }

        ctx.Reflect.Notify(diff.Keys.ToList(), (target, name) =>
        {
            var (oldSymbol, newSymbol, flag1, flag2) = diff[name];
            var symbol3 = target.IsolateMap[name] as CordisSymbol;
            var flag3 = target.GetProp(delims[name]) as CordisSymbol;
            return (Equals(oldSymbol, symbol3) || Equals(newSymbol, symbol3)) && Equals(flag1, flag3) != Equals(flag1, flag2);
        });

        foreach (var (name, delim) in delims)
        {
            // 与 TS 的 Reflect.ownKeys(newMap) 对齐,只看本层 isolate 声明。
            if (!newMap.HasOwn(name))
            {
                entry.Ctx.DeleteOwn(delim);
            }
        }

        return null;
    }

    private static CascadeMap ToCascade(Dictionary<string, object?>? dict)
    {
        var map = new CascadeMap();
        if (dict is not null)
        {
            foreach (var (key, value) in dict) map[key] = value;
        }
        return map;
    }

    private static void CollectRealms(
        Entry entry,
        EntryOptions legacy,
        bool active,
        Dictionary<string, GlobalRealm> realms,
        Context ctx)
    {
        foreach (var (name, label) in legacy.Isolate ?? new Dictionary<string, object?>())
        {
            if (label is not string text) continue;
            if (active && entry.Options.Isolate?.GetOrNull(name) as string == text) continue;
            if (!realms.TryGetValue(text, out var realm)) continue;

            foreach (var other in ctx.Get<Loader>("loader")!.Entries())
            {
                if (other.Options.Isolate?.GetOrNull(name) as string == realm.Label) return;
            }
            realm.Delete(name);
            if (realm.Size == 0)
            {
                realms.Remove(realm.Label);
            }
        }
    }
}
