using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Dsh.Plugins;

/** 配置项的类型族: 由生成器按声明类型归类, 文档/面板据此选择写法与控件。 */
public enum PluginSettingKind
{
    Text,
    Boolean,
    Integer,
    Number,
    List,
    Mapping,
    Unknown,
}

/** 一个可配置项: 键名/类型/默认值字面量(生成期从声明读出)/说明(可后补)/嵌套子项。 */
public sealed record PluginSettingSpec(
    string Key,
    PluginSettingKind Kind,
    string? DefaultLiteral = null,
    string? Description = null,
    IReadOnlyList<PluginSettingSpec>? Children = null,
    bool DynamicKeys = false);

/**
 * 插件的配置面: 生成器在插件程序集里按 ConfigType 或 [DshPluginSettings] 标注的类型产出实现
 * (键名/默认值都是编译期字面量, 运行期零反射/裁剪安全), 供 settings 模板、配置校验与可视化编辑共用。
 */
public interface ISettingsSchemaProvider
{
    /** 文档(顶层 settings)或插件包名。 */
    string Package { get; }

    IReadOnlyList<PluginSettingSpec> Settings { get; }

    /** true=插件参数(默认); false=顶层 settings 文档。 */
    bool IsPlugin => true;
}

/**
 * 配置面登记表: 生成器在程序集的模块初始化器里注册(编译进镜像的与运行期装载的都会跑到),
 * 因此枚举配置面不需要反射、不依赖装载顺序之外的任何状态。
 * 同包的不同加载上下文分别保留快照, 最新注册者生效; 卸载后恢复此前仍存活的注册者。
 * 登记时**拷贝数据**而不是保留 provider 实例: 动态加载的插件卸载后其 ALC 才能被回收。
 */
public static class DshPluginSettingsRegistry
{
    private static readonly List<Snapshot> Providers = [];
    private static readonly Lock Gate = new();
    private static readonly ConditionalWeakTable<AssemblyLoadContext, object> Owners = new();

    public static void Register(ISettingsSchemaProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var context = AssemblyLoadContext.GetLoadContext(provider.GetType().Assembly) ?? AssemblyLoadContext.Default;
        var owner = Owners.GetValue(context, static _ => new object());
        var snapshot = new Snapshot(provider.Package, provider.IsPlugin, [.. provider.Settings], owner);
        lock (Gate)
        {
            Providers.RemoveAll(existing => ReferenceEquals(existing.Owner, owner)
                && string.Equals(existing.Package, snapshot.Package, StringComparison.Ordinal));
            Providers.Add(snapshot);
        }
        // A plugin's settings leave with its load context; only the context references the handler, so the context stays collectible.
        if (context.IsCollectible)
            context.Unloading += _ => Forget(snapshot);
    }

    private static void Forget(Snapshot snapshot)
    {
        lock (Gate)
            Providers.RemoveAll(existing => ReferenceEquals(existing, snapshot));
    }

    /** 已注册的配置面(文档在前, 其余按包名序); 快照返回, 调用方不必持有锁。 */
    public static IReadOnlyList<ISettingsSchemaProvider> All()
    {
        lock (Gate)
        {
            return
            [
                .. Providers
                    .GroupBy(provider => provider.Package, StringComparer.Ordinal)
                    .Select(group => group.Last())
                    .OrderByDescending(provider => !provider.IsPlugin)
                    .ThenBy(provider => provider.Package, StringComparer.Ordinal),
            ];
        }
    }

    private sealed record Snapshot(string Package, bool IsPlugin, IReadOnlyList<PluginSettingSpec> Settings, object Owner) : ISettingsSchemaProvider;
}

/**
 * 标注插件参数的绑定/声明类型: 标在类型上表示"这个类型的可写属性就是插件参数", 标在属性上可覆盖键名。
 * 只声明不改变绑定行为(未声明 IDshPlugin.ConfigType 的插件照旧自己解析参数), 因此加标注是零风险动作。
 */
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property, AllowMultiple = true)]
public sealed class DshPluginSettingsAttribute : Attribute
{
    /** 属性上的键名覆盖(如 MaxPoints → max_points); 类型上不用。 */
    public string? Name { get; set; }
}

/** 标注顶层 settings 文档类型(如 HarnessSettings): 生成器据此产出"文档"配置面。 */
[AttributeUsage(AttributeTargets.Class)]
public sealed class DshSettingsDocumentAttribute : Attribute;

