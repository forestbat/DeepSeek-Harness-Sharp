namespace Cordis;

public class Context
{
    internal static readonly CordisSymbol FiberKey = CordisSymbol.For("cordis.slot.fiber");
    internal static readonly CordisSymbol EventsKey = CordisSymbol.For("cordis.slot.events");
    internal static readonly CordisSymbol RegistryKey = CordisSymbol.For("cordis.slot.registry");
    internal static readonly CordisSymbol ReflectKey = CordisSymbol.For("cordis.slot.reflect");
    internal static readonly CordisSymbol LoggerKey = CordisSymbol.For("cordis.slot.logger");
    internal static readonly CordisSymbol BaseUrlKey = CordisSymbol.For("cordis.slot.baseUrl");

    private readonly Dictionary<object, object?> _own = new(ReferenceEqualityComparer.Instance);
    private Context? _prototype;

    public Context Root { get; }

    public Context()
    {
        Root = this;
        _own[Symbols.Isolate] = new CascadeMap();
        _own[Symbols.Intercept] = new CascadeMap();
        _own[FiberKey] = new Fiber(this, null, [], null);
        _own[ReflectKey] = new ReflectService(this);
        _own[RegistryKey] = new RegistryService(this);
        _own[EventsKey] = new EventsService(this);
        _own[LoggerKey] = new LoggerService(this);
        Fiber.Disposables.Clear();
    }

    private Context(Context prototype)
    {
        _prototype = prototype;
        Root = prototype.Root;
    }

    public Context Extend(params (object Key, object? Value)[] meta)
    {
        var child = new Context(this);
        foreach (var (key, value) in meta)
        {
            child._own[key] = value;
        }
        return child;
    }

    public object? GetProp(object key)
    {
        for (var ctx = this; ctx is not null; ctx = ctx._prototype)
        {
            if (ctx._own.TryGetValue(key, out var value)) return value;
        }
        return null;
    }

    public void SetOwn(object key, object? value) => _own[key] = value;

    public bool HasOwn(object key) => _own.ContainsKey(key);

    public bool DeleteOwn(object key) => _own.Remove(key);

    public IEnumerable<object> OwnKeys => _own.Keys;

    internal void SetPrototype(Context prototype) => _prototype = prototype;

    public Fiber Fiber => (Fiber)GetProp(FiberKey)!;
    public EventsService Events => ((EventsService)GetProp(EventsKey)!).Bind(this);
    public RegistryService Registry => ((RegistryService)GetProp(RegistryKey)!).Bind(this);
    public ReflectService Reflect => ((ReflectService)GetProp(ReflectKey)!).Bind(this);
    public LoggerService Logger => ((LoggerService)GetProp(LoggerKey)!).Bind(this);

    public string? BaseUrl
    {
        get => (string?)GetProp(BaseUrlKey);
        set => SetOwn(BaseUrlKey, value);
    }

    public CascadeMap IsolateMap => (CascadeMap)GetProp(Symbols.Isolate)!;
    public CascadeMap InterceptMap => (CascadeMap)GetProp(Symbols.Intercept)!;

    public Func<Context, bool>? Filter => GetProp(Symbols.Filter) as Func<Context, bool>;

    public Context Isolate(string name, CordisSymbol? label = null)
    {
        var shadow = IsolateMap.Derive();
        shadow[name] = label ?? CordisSymbol.New(name);
        return Extend((Symbols.Isolate, shadow));
    }

    public Context Intercept(string name, object? config)
    {
        var intercept = InterceptMap.Derive();
        intercept[name] = config;
        return Extend((Symbols.Intercept, intercept));
    }

    public object? Get(string name, bool strict = true) => Reflect.Get(name, strict);

    public T? Get<T>(string name, bool strict = true) where T : class => Reflect.Get(name, strict) as T;

    public void Set(string name, object? value) => Reflect.Set(name, value);

    public EffectHandle Provide(string name, object? value = null, Func<bool>? check = null) => Reflect.Provide(name, value, check);

    public EffectHandle Accessor(string name, AccessorProperty property) => Reflect.Accessor(name, property);

    public EffectHandle Mixin(string source, params string[] names) => Reflect.Mixin(source, names);

    public Func<bool> On(object name, EventListener listener, EventOptions? options = null) => Events.On(name, listener, options);

    public Func<bool> Once(object name, EventListener listener, EventOptions? options = null) => Events.Once(name, listener, options);

    public void Emit(object name, params object?[] args) => Events.Emit(null, name, args);

    public Task Parallel(object name, params object?[] args) => Events.Parallel(null, name, args);

    public ValueTask<object?> Serial(object name, params object?[] args) => Events.Serial(null, name, args);

    public ValueTask<object?> Bail(object name, params object?[] args) => Events.Bail(null, name, args);

    public EffectHandle Effect(Func<object?> execute, string label = "anonymous") => Fiber.Effect(execute, label);

    public Fiber Plugin(object plugin, object? config = null) => Registry.Plugin(plugin, config);

    public Fiber Inject(Dictionary<string, object?> inject, Func<Context, object?, object?> callback) => Registry.Inject(inject, callback);

    public Logger LoggerFor(string? name = null) => Logger.Invoke(name);

    public override string ToString() => $"Context <{Fiber.Name}>";

    internal object? ResolveProperty(string name)
    {
        var error = new CordisException("NO_INJECT", $"cannot get property \"{name}\" without inject");
        if (Reflect.Props.TryGetValue(name, out var def) && def is AccessorProperty accessor)
        {
            return accessor.Get(this, null, error);
        }
        if (Fiber.Runtime is null) return Reflect.Get(name, false);
        return Events.WaterfallSync(this, EventNames.Get, [this, name, error], () => WalkFibers(name, error));
    }

    private object? WalkFibers(string name, CordisException error)
    {
        var key = Root.IsolateMap[name] as CordisSymbol;
        var fiber = Fiber;
        while (true)
        {
            if (fiber.Store is not null && fiber.Store.TryGetValue(name, out var impl)) return impl.Value;
            if (fiber.Inject.ContainsKey(name))
            {
                throw new CordisException("INACTIVE_CONTEXT", $"cannot get required service \"{name}\" in inactive context");
            }
            if (fiber.Runtime is null) throw error;
            var parentKey = fiber.Parent.IsolateMap[name] as CordisSymbol;
            if (!Equals(parentKey, key)) throw error;
            fiber = fiber.Parent.Fiber;
        }
    }

    internal void SetProperty(string name, object? value)
    {
        var error = new CordisException("NO_PROVIDE", $"cannot set property \"{name}\" without provide");
        if (!Reflect.Props.TryGetValue(name, out var def))
        {
            if (Fiber.Runtime is null)
            {
                SetOwn(name, value);
                return;
            }
            throw error;
        }
        if (def is AccessorProperty accessor)
        {
            if (accessor.Set is null) return;
            accessor.Set(this, value, null, error);
            return;
        }
        Events.WaterfallSync(this, EventNames.Set, [this, name, value, error], () => Reflect.Set(name, value, error));
    }
}
