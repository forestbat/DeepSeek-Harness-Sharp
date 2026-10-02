using System.Diagnostics.CodeAnalysis;
using Dsh.Runtime;

namespace Dsh.Plugins;

/** 插件登记表:每包一条,工厂直接构造实例;形态、能力与入口由描述符承载。 */
public sealed class PluginCatalog
{
    private readonly Dictionary<string, PluginHolder> _plugins = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> PackageNames => _plugins.Keys;

    public IReadOnlyList<PluginDescriptor> Descriptors
        => _plugins.Values.Select(holder => holder.Descriptor).ToList();

    /** 包名全局唯一:后登记者拒绝,先登记者优先;重复登记是打包/组合错误,必须显式抛错而非静默覆盖。 */
    public void Register(PluginDescriptor descriptor, Func<IDshPlugin> create)
    {
        if (_plugins.TryGetValue(descriptor.Package, out var existing))
            throw new InvalidOperationException(
                $"duplicate plugin package: {descriptor.Package} (already registered as {existing.Descriptor.Form}, refused {descriptor.Form})");
        _plugins[descriptor.Package] = new PluginHolder(descriptor, create);
    }

    public bool TryDescribe(string packageName, [NotNullWhen(true)] out PluginDescriptor? descriptor)
    {
        if (_plugins.TryGetValue(packageName, out var holder))
        {
            descriptor = holder.Descriptor;
            return true;
        }
        descriptor = null;
        return false;
    }

    public bool TryGet(string packageName, [NotNullWhen(true)] out Func<IDshPlugin>? create)
    {
        if (_plugins.TryGetValue(packageName, out var holder))
        {
            create = holder.Create;
            return true;
        }
        create = null;
        return false;
    }

    public PluginDefinition CreateDefinition(string packageName)
    {
        if (!_plugins.TryGetValue(packageName, out var holder))
            throw new KeyNotFoundException($"Plugin package '{packageName}' is not registered.");
        return holder.ToDefinition();
    }

    public bool TryCreateDefinition(string packageName, out PluginDefinition? definition)
    {
        if (!_plugins.TryGetValue(packageName, out var holder))
        {
            definition = null;
            return false;
        }
        definition = holder.ToDefinition();
        return true;
    }

    public void Remove(string packageName) => _plugins.Remove(packageName);

    private sealed record PluginHolder(PluginDescriptor Descriptor, Func<IDshPlugin> Create)
    {
        /** 与旧反射路径同语义:探测实例取 Inject/InjectTypes/ConfigType,每次 Apply 构造新实例。
         *  声明了 ConfigType 的插件在 Apply 前把 parameters 绑定成该类型,未知字段在激活期 WARN。 */
        public PluginDefinition ToDefinition()
        {
            var probe = Create();
            return new PluginDefinition
            {
                Name = Descriptor.Package,
                Inject = probe.Inject,
                InjectTypes = probe.InjectTypes,
                ConfigType = probe.ConfigType,
                Apply = (ctx, config) => Create().Apply(ctx, BindConfig(probe.ConfigType, config, ctx)),
            };
        }

        private static object? BindConfig(Type? configType, object? config, Context ctx)
            => configType is null
                ? config
                : PluginConfigBinding.Bind(configType, config, message => ctx.LoggerFor("config").Warn("%s", message));
    }
}
