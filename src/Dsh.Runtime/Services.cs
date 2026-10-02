using Dsh.Runtime.Ioc;

namespace Dsh.Runtime;

internal sealed class ServiceImpl
{
    public required string Name { get; init; }
    public Func<bool>? Check { get; init; }
    public required PluginActivation Owner { get; init; }
}

/** 服务元数据(激活门控/所有权/重复检查);实例本身存放在 IServiceRegistry。 */
internal sealed class ServiceTable(IServiceRegistry registry)
{
    private readonly Dictionary<string, ServiceImpl> _services = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    public object? Get(string name, bool strict)
    {
        ServiceImpl? impl;
        lock (_sync)
            _services.TryGetValue(name, out impl);
        if (impl is null)
            return null;
        if (strict && impl.Owner.State != ActivationState.Active)
            return null;
        return registry.Resolve(name);
    }

    public bool IsInjectable(string name)
    {
        ServiceImpl? impl;
        lock (_sync)
            _services.TryGetValue(name, out impl);
        if (impl is null || impl.Owner.State != ActivationState.Active)
            return false;
        if (impl.Check is null)
            return true;
        try
        {
            return impl.Check();
        }
        catch (Exception error)
        {
            impl.Owner.Ctx.Logger.Error("%s", error);
            return false;
        }
    }

    public EffectHandle Provide(Context ctx, string name, object? value, Func<bool>? check)
    {
        var impl = new ServiceImpl { Name = name, Check = check, Owner = ctx.Activation };
        lock (_sync)
        {
            if (_services.TryGetValue(name, out var occupied))
                throw new RuntimeException("SERVICE_REGISTERED", $"service \"{name}\" has been registered at <{occupied.Owner.Name}>");
            _services[name] = impl;
        }
        registry.Register(name, value);
        ctx.Root.Scheduler.OnServiceProvided(name, ctx.Activation);
        ctx.Activation.TrackProvided(name);
        var effect = new EffectHandle($"provide({name})", () =>
        {
            lock (_sync)
                _services.Remove(name);
            registry.Unregister(name);
            ctx.Root.Scheduler.OnServiceRemoved(name, ctx.Activation);
            return Task.CompletedTask;
        });
        ctx.Activation.Effects.Add(effect);
        return effect;
    }

    internal IServiceRegistry Registry => registry;

    /** 启动 Pending 汇总用:报告服务的提供者与提供者状态;未注册返回 null。 */
    internal (string OwnerName, ActivationState OwnerState)? DescribeProvider(string name)
    {
        lock (_sync)
            return _services.TryGetValue(name, out var impl)
                ? (impl.Owner.Name, impl.Owner.State)
                : null;
    }
}
