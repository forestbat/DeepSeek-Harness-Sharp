namespace Cordis;

public interface IConfigMerger
{
    object? Merge(IReadOnlyList<object?> configs);
}

public abstract class Service
{
    public string Name { get; }
    public Context Ctx { get; private set; }

    protected Service(Context ctx, string name)
    {
        Ctx = ctx;
        Name = name;
        ctx.Reflect.Provide(name, this, Check);
    }

    protected virtual bool Check() => true;

    internal Service Bind(Context ctx)
    {
        var copy = (Service)MemberwiseClone();
        copy.Ctx = ctx;
        return copy;
    }

    protected object? ResolveConfig(object? baseConfig = null, object? head = null)
    {
        var configs = new List<object?>();
        var chain = Ctx.InterceptMap.Chain().ToList();
        chain.Reverse();
        foreach (var level in chain)
        {
            if (level.HasOwn(Name)) configs.Add(level[Name]);
        }
        if (baseConfig is not null) configs.Insert(0, baseConfig);
        if (head is not null) configs.Add(head);
        if (this is IConfigMerger merger) return merger.Merge(configs);
        var result = new Dictionary<string, object?>();
        foreach (var config in configs)
        {
            if (config is not IDictionary<string, object?> dict) continue;
            foreach (var (key, value) in dict) result[key] = value;
        }
        return result;
    }
}
