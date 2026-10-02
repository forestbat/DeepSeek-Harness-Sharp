using Dsh.Runtime.Ioc;

namespace Dsh.Runtime;

internal sealed class ServiceImpl
{
    public required string Name { get; init; }
    public Func<bool>? Check { get; init; }
    public required PluginActivation Owner { get; init; }

    /** 类型索引:注册实例的运行时类型与其全部接口,供按契约类型匹配;实例为 null 时为空。 */
    public required IReadOnlyList<Type> Types { get; init; }
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

    /** 按类型取用:唯一激活候选直接解析;多个激活候选是歧义,抛错并列出可选名字;无候选返回 null。 */
    public object? GetByType(Type type, bool strict)
    {
        var active = ActiveCandidates(type, strict);
        if (active.Count > 1)
            throw new RuntimeException("AMBIGUOUS_SERVICE",
                $"multiple services assignable to {type.Name}: {string.Join(", ", active.Select(impl => impl.Name))}; pick one by name");
        return active.Count == 1 ? registry.Resolve(active[0].Name) : null;
    }

    public bool IsInjectable(string name)
    {
        ServiceImpl? impl;
        lock (_sync)
            _services.TryGetValue(name, out impl);
        return impl is not null && IsUsable(impl);
    }

    /** 类型边的激活门控:恰好一个可注入候选才算可用,零个是缺失、多个是歧义,都按不满足处理。 */
    public bool IsInjectable(Type type) => ActiveCandidates(type, strict: true).Count == 1;

    /** 激活成功时把类型边落成名字边:返回唯一激活候选的服务名,无唯一解返回 null。 */
    internal string? ResolveName(Type type) => ActiveCandidates(type, strict: true) is [var single] ? single.Name : null;

    /** Pending 汇总用:列出类型索引可赋给 type 的全部注册服务及其提供者状态。 */
    internal IReadOnlyList<string> DescribeCandidates(Type type)
    {
        List<ServiceImpl> matches;
        lock (_sync)
            matches = _services.Values.Where(impl => impl.Types.Any(type.IsAssignableFrom)).ToList();
        return matches.Select(impl => $"{impl.Name} (provided by <{impl.Owner.Name}>, state: {impl.Owner.State})").ToList();
    }

    /** 激活候选:提供方 Active(非 strict 模式不限状态)且 Check 通过。 */
    private List<ServiceImpl> ActiveCandidates(Type type, bool strict)
    {
        List<ServiceImpl> matches;
        lock (_sync)
            matches = _services.Values.Where(impl => impl.Types.Any(type.IsAssignableFrom)).ToList();
        return matches.Where(impl => IsUsable(impl, strict)).ToList();
    }

    private static bool IsUsable(ServiceImpl impl, bool strict = true)
    {
        if (strict && impl.Owner.State != ActivationState.Active)
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
        var impl = new ServiceImpl { Name = name, Check = check, Owner = ctx.Activation, Types = TypeIndexOf(value) };
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

    /** 类型索引只取实例形状(运行时类型 + 接口),无代码生成、无动态泛型,裁剪/AOT 安全。 */
    private static IReadOnlyList<Type> TypeIndexOf(object? value)
    {
        if (value is null)
            return [];
        var runtime = value.GetType();
        return [runtime, .. runtime.GetInterfaces()];
    }

    /** 启动 Pending 汇总用:报告服务的提供者与提供者状态;未注册返回 null。 */
    internal (string OwnerName, ActivationState OwnerState)? DescribeProvider(string name)
    {
        lock (_sync)
            return _services.TryGetValue(name, out var impl)
                ? (impl.Owner.Name, impl.Owner.State)
                : null;
    }
}
