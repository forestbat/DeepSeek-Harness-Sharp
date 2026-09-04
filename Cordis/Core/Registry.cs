namespace Cordis;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class InjectAttribute : Attribute
{
    public string Name { get; }
    public object? Config { get; }

    public InjectAttribute(string name, object? config = null)
    {
        Name = name;
        Config = config;
    }
}

public abstract class PluginCallback
{
    public abstract object? Invoke(Context ctx, object? config);
}

public sealed class DelegatePluginCallback(Func<Context, object?, object?> apply) : PluginCallback
{
    public override object? Invoke(Context ctx, object? config) => apply(ctx, config);
}

public sealed class ObjectPluginCallback(IPluginObject plugin) : PluginCallback
{
    public override object? Invoke(Context ctx, object? config) => plugin.Apply(ctx, config);
}

public interface IPluginObject
{
    object? Apply(Context ctx, object? config);
}

public interface IAsyncInit
{
    IAsyncEnumerable<object?> Init();
}

public sealed class ClassPluginCallback(Type type) : PluginCallback
{
    private static readonly Dictionary<Type, ClassPluginCallback> Cache = new();

    public Type PluginType => type;

    public static ClassPluginCallback Of(Type type)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(type, out var callback)) return callback;
            return Cache[type] = new ClassPluginCallback(type);
        }
    }

    public override object? Invoke(Context ctx, object? config)
    {
        var instance = Activator.CreateInstance(type, ctx, config)
            ?? throw new CordisException("INVALID_PLUGIN", $"cannot construct plugin {type.Name}");
        if (instance is IAsyncInit init) return init.Init();
        return null;
    }
}

public sealed class PluginRuntime
{
    public string? Name { get; init; }
    public required PluginCallback Callback { get; init; }
    public DisposableList<Fiber> Fibers { get; } = new();
    public IConfigValidator? ConfigValidator { get; init; }
}

public sealed class PluginDefinition
{
    public string? Name { get; init; }
    public Dictionary<string, object?>? Inject { get; init; }
    public IConfigValidator? ConfigValidator { get; init; }
    public required PluginCallback Callback { get; init; }

    public static PluginDefinition From(Func<Context, object?, object?> apply, string? name = null,
        Dictionary<string, object?>? inject = null, IConfigValidator? configValidator = null)
    {
        return new PluginDefinition
        {
            Name = name ?? apply.Method.Name,
            Inject = inject,
            ConfigValidator = configValidator,
            Callback = new DelegatePluginCallback(apply),
        };
    }
}

public sealed class RegistryService
{
    private sealed class State
    {
        public int Counter;
        public readonly Dictionary<PluginCallback, PluginRuntime> Internal = new(ReferenceEqualityComparer.Instance);
    }

    private readonly State _state;

    public Context Ctx { get; }

    public RegistryService(Context ctx)
    {
        Ctx = ctx;
        _state = new State();
    }

    private RegistryService(Context ctx, State state)
    {
        Ctx = ctx;
        _state = state;
    }

    public RegistryService Bind(Context ctx) => new(ctx, _state);

    internal int Counter => ++_state.Counter;

    public int Size => _state.Internal.Count;

    public PluginCallback? Resolve(object? plugin)
    {
        try
        {
            return plugin switch
            {
                null => null,
                PluginCallback callback => callback,
                PluginDefinition def => def.Callback,
                Type type => ClassPluginCallback.Of(type),
                IPluginObject obj => new ObjectPluginCallback(obj),
                Func<Context, object?, object?> func => new DelegatePluginCallback(func),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    public PluginRuntime? Get(object plugin)
    {
        var key = Resolve(plugin);
        return key is not null && _state.Internal.TryGetValue(key, out var runtime) ? runtime : null;
    }

    public bool Has(object plugin)
    {
        var key = Resolve(plugin);
        return key is not null && _state.Internal.ContainsKey(key);
    }

    public PluginRuntime? Delete(object plugin)
    {
        var key = Resolve(plugin);
        if (key is null || !_state.Internal.Remove(key, out var runtime)) return null;
        foreach (var fiber in runtime.Fibers)
        {
            fiber.Dispose();
        }
        return runtime;
    }

    public IEnumerable<PluginCallback> Keys() => _state.Internal.Keys;

    public IEnumerable<PluginRuntime> Values() => _state.Internal.Values;

    public Fiber Inject(Dictionary<string, object?> inject, Func<Context, object?, object?> callback)
    {
        return Plugin(new PluginDefinition
        {
            Name = callback.Method.Name,
            Inject = inject,
            Callback = new DelegatePluginCallback(callback),
        }, null);
    }

    public Fiber Plugin(object plugin, object? config = null)
    {
        var callback = Resolve(plugin)
            ?? throw new CordisException("INVALID_PLUGIN",
                $"invalid plugin, expect function or object with an apply method, received {plugin?.GetType().Name ?? "null"}");
        Ctx.Fiber.AssertActive();

        if (!_state.Internal.TryGetValue(callback, out var runtime))
        {
            var name = ResolveName(plugin, callback);
            runtime = new PluginRuntime
            {
                Name = name,
                Callback = callback,
                ConfigValidator = ResolveConfigValidator(plugin),
            };
            _state.Internal[callback] = runtime;
        }

        return new Fiber(Ctx, config, ResolveInject(plugin), runtime);
    }

    private static string? ResolveName(object plugin, PluginCallback callback)
    {
        var name = plugin switch
        {
            PluginDefinition def => def.Name,
            Type type => type.Name,
            Func<Context, object?, object?> func => func.Method.Name,
            _ => plugin.GetType().Name,
        };
        return name == "apply" ? null : name;
    }

    private static IConfigValidator? ResolveConfigValidator(object plugin)
    {
        return plugin switch
        {
            PluginDefinition def => def.ConfigValidator,
            Type type => type.GetProperty("Config", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                ?.GetValue(null) as IConfigValidator,
            _ => null,
        };
    }

    internal static Dictionary<string, object?> ResolveInject(object plugin)
    {
        var result = new Dictionary<string, object?>();
        switch (plugin)
        {
            case PluginDefinition def when def.Inject is not null:
                foreach (var (key, value) in def.Inject) result[key] = value;
                break;
            case Type type:
                for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
                {
                    foreach (var attr in t.GetCustomAttributes(typeof(InjectAttribute), false).Cast<InjectAttribute>())
                    {
                        result[attr.Name] = attr.Config;
                    }
                }
                break;
        }
        return result;
    }
}
