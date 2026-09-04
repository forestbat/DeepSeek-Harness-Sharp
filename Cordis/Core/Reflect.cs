namespace Cordis;

public sealed class Impl
{
    public required string Name { get; init; }
    public required Fiber Fiber { get; init; }
    public object? Value { get; set; }
    public Func<bool>? Check { get; init; }
}

public abstract class ContextProperty
{
    public const string ServiceType = "service";
    public const string AccessorType = "accessor";

    public abstract string Type { get; }
}

public sealed class ServiceProperty : ContextProperty
{
    public override string Type => ServiceType;
}

public sealed class AccessorProperty : ContextProperty
{
    public override string Type => AccessorType;
    public required Func<Context, object?, Exception, object?> Get { get; init; }
    public Func<Context, object?, object?, Exception, bool>? Set { get; init; }
}

public sealed class ReflectService
{
    public Context Ctx { get; }
    internal Dictionary<CordisSymbol, Impl> Store { get; }
    internal Dictionary<string, ContextProperty> Props { get; }

    public ReflectService(Context ctx)
    {
        Ctx = ctx;
        Store = new Dictionary<CordisSymbol, Impl>();
        Props = new Dictionary<string, ContextProperty>();
    }

    private ReflectService(Context ctx, ReflectService prototype)
    {
        Ctx = ctx;
        Store = prototype.Store;
        Props = prototype.Props;
    }

    public ReflectService Bind(Context ctx) => new(ctx, this);

    public object? Get(string name, bool strict = true)
    {
        return GetImpl(name, strict)?.Value;
    }

    internal Impl? GetImpl(string name, bool strict = true)
    {
        var key = Ctx.IsolateMap[name] as CordisSymbol;
        var impl = key is not null && Store.TryGetValue(key, out var i) ? i : null;
        if (impl is null) return null;
        if (strict && impl.Fiber.State != FiberState.Active) return null;
        return impl;
    }

    internal bool Set(string name, object? value, Exception? error = null)
    {
        var key = Ctx.IsolateMap[name] as CordisSymbol;
        if (key is null || !Store.TryGetValue(key, out var impl))
        {
            throw new CordisException("NOT_PROVIDED", $"cannot set property \"{name}\" without provide");
        }
        if (!ReferenceEquals(impl.Fiber, Ctx.Fiber))
        {
            throw new CordisException("MULTIPLE_FIBERS", $"cannot set property \"{name}\" in multiple fibers");
        }
        impl.Value = value;
        return true;
    }

    public EffectHandle Provide(string name, object? value = null, Func<bool>? check = null)
    {
        return Ctx.Fiber.Effect(() =>
        {
            if (!Props.TryGetValue(name, out var existing))
            {
                Props[name] = new ServiceProperty();
            }
            else if (existing.Type != ContextProperty.ServiceType)
            {
                throw new CordisException("PROPERTY_DECLARED", $"property \"{name}\" is already declared as {existing.Type}");
            }

            var rootIsolate = Ctx.Root.IsolateMap;
            if (rootIsolate[name] is not CordisSymbol)
            {
                rootIsolate[name] = CordisSymbol.New(name);
            }
            var key = (CordisSymbol)Ctx.IsolateMap[name]!;
            var impl = new Impl { Name = name, Value = value, Fiber = Ctx.Fiber, Check = check };
            if (Store.TryGetValue(key, out var occupied))
            {
                throw new CordisException("SERVICE_REGISTERED", $"service \"{name}\" has been registered at <{occupied.Fiber.Name}>");
            }
            Store[key] = impl;
            Ctx.Fiber.Store![name] = impl;
            if (Ctx.Fiber.State == FiberState.Active)
            {
                Notify([name]);
            }
            return (Func<Task>)(async () =>
            {
                Store.Remove(key);
                var fibers = Notify([name]);
                foreach (var fiber in fibers)
                {
                    try
                    {
                        await fiber.Await();
                    }
                    catch
                    {
                        // 与 JS 的 Promise.allSettled 一致：忽略依赖方重建失败
                    }
                }
                Ctx.Fiber.Store!.Remove(name);
            });
        }, $"ctx.provide({name})");
    }

    public List<Fiber> Notify(IReadOnlyList<string> names, Func<Context, string, bool>? filter = null)
    {
        filter ??= (ctx, name) => Equals(ctx.IsolateMap[name], Ctx.IsolateMap[name]);
        var fibers = new List<Fiber>();
        foreach (var runtime in Ctx.Registry.Values())
        {
            foreach (var fiber in runtime.Fibers)
            {
                var hasUpdate = false;
                foreach (var name in names)
                {
                    if (!fiber.Inject.ContainsKey(name)) continue;
                    if (!filter(fiber.Ctx, name)) continue;
                    hasUpdate = true;
                    fiber.CheckImpl(name);
                }
                if (!hasUpdate) continue;
                fiber.Refresh();
                fibers.Add(fiber);
            }
        }
        foreach (var name in names)
        {
            var self = Ctx.Extend();
            self.SetOwn(Symbols.Filter, (Func<Context, bool>)(target => filter(target, name)));
            Ctx.Events.Emit(self, EventNames.Service, name, GetImpl(name, false)?.Value);
        }
        return fibers;
    }

    public EffectHandle Accessor(string name, AccessorProperty property)
    {
        return Ctx.Fiber.EffectSync(() =>
        {
            if (Props.ContainsKey(name))
            {
                throw new CordisException("PROPERTY_DECLARED", $"property \"{name}\" is already declared as {Props[name].Type}");
            }
            Props[name] = property;
            return () => Props.Remove(name);
        }, $"ctx.accessor({name})");
    }

    public EffectHandle Mixin(string source, IReadOnlyDictionary<string, string> mixins)
    {
        return Ctx.Fiber.Effect(() =>
        {
            var handles = new List<EffectHandle>();
            foreach (var (key, value) in mixins)
            {
                handles.Add(Accessor(value, new AccessorProperty
                {
                    Get = (ctx, receiver, error) =>
                    {
                        var service = GetServiceForMixin(ctx, source, error);
                        return service is null ? null : GetMember(service, key);
                    },
                    Set = (ctx, value0, receiver, error) =>
                    {
                        var service = GetServiceForMixin(ctx, source, error);
                        if (service is null) return false;
                        SetMember(service, key, value0);
                        return true;
                    },
                }));
            }
            return () =>
            {
                foreach (var handle in handles) handle.Dispose();
            };
        }, $"ctx.mixin({source})");
    }

    public EffectHandle Mixin(string source, params string[] names)
    {
        return Mixin(source, names.ToDictionary(n => n, n => n));
    }

    private static object? GetServiceForMixin(Context ctx, string source, Exception error)
    {
        return ctx.ResolveProperty(source);
    }

    private static object? GetMember(object service, string name)
    {
        var type = service.GetType();
        var method = type.GetMethods().FirstOrDefault(m => m.Name == name && m.GetParameters().Length >= 0);
        if (method is not null)
        {
            return new ServiceMethod(service, method);
        }
        var prop = type.GetProperty(name);
        if (prop is not null) return prop.GetValue(service);
        var field = type.GetField(name);
        if (field is not null) return field.GetValue(service);
        throw new CordisException("MEMBER_NOT_FOUND", $"member \"{name}\" not found on {type.Name}");
    }

    private static void SetMember(object service, string name, object? value)
    {
        var type = service.GetType();
        var prop = type.GetProperty(name);
        if (prop is not null)
        {
            prop.SetValue(service, value);
            return;
        }
        var field = type.GetField(name);
        if (field is not null)
        {
            field.SetValue(service, value);
            return;
        }
        throw new CordisException("MEMBER_NOT_FOUND", $"member \"{name}\" not found on {type.Name}");
    }
}

public sealed class ServiceMethod(object target, System.Reflection.MethodInfo method)
{
    public object? Invoke(object?[] args)
    {
        var parameters = method.GetParameters();
        var converted = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            converted[i] = i < args.Length ? args[i] : parameters[i].DefaultValue;
        }
        return method.Invoke(target, converted);
    }
}
