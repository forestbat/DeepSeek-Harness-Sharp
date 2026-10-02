using Dsh.Runtime.Events;

namespace Dsh.Runtime.Composition;

public sealed record PluginEntry(PluginDefinition Definition, object? Config);

public sealed class Composition
{
    private readonly Context _root;

    private Composition(Context root)
    {
        _root = root;
    }

    public IReadOnlyList<PluginActivation> Activations => _root.Scheduler.Snapshot();

    public Context Root => _root;

    public PluginActivation? Find(string packageName)
        => Activations.FirstOrDefault(activation => string.Equals(activation.Name, packageName, StringComparison.Ordinal));

    public static async Task<Composition> StartAsync(Context root, IReadOnlyList<PluginEntry> entries)
    {
        foreach (var entry in entries)
            root.Scheduler.Register(entry.Definition, entry.Config);

        await root.Scheduler.SettleAsync();
        WarnPending(root);
        WarnFailed(root);
        root.Emit(new CompositionReadyNotification());
        return new Composition(root);
    }

    public async Task<PluginActivation> AddAsync(PluginDefinition definition, object? config = null)
        => await _root.Scheduler.AddAsync(definition, config);

    /** 关闭时释放所有插件的效果:编译进镜像的插件不经过协作式卸载,文件句柄/订阅这类资源必须在这里放掉。 */
    public void DeactivateAll()
    {
        foreach (var activation in Activations)
            activation.DisposeEffectsAsync().GetAwaiter().GetResult();
    }

    /** 依赖未满足而停在 Pending 的插件不进失败列表,但必须显式 WARN:缺哪个服务、谁能提供,一眼可查。 */
    private static void WarnPending(Context root)
    {
        var pending = root.Scheduler.Snapshot()
            .Where(activation => activation.State == ActivationState.Pending)
            .ToList();
        if (pending.Count == 0)
            return;
        var logger = root.LoggerFor("composition");
        foreach (var activation in pending)
        {
            var missing = activation.Inject
                .Where(name => !root.IsServiceInjectable(name))
                .Select(name => DescribeMissing(root, name))
                .Concat(activation.InjectTypes
                    .Where(type => !root.IsServiceInjectable(type))
                    .Select(type => DescribeMissing(root, type)))
                .ToList();
            logger.Warn($"plugin <{activation.Name}> pending (dependencies missing): {string.Join(", ", missing)}");
        }
    }

    private static string DescribeMissing(Context root, string service)
    {
        var provider = root.ServiceTable.DescribeProvider(service);
        return provider is null
            ? $"{service} (no provider registered; providing plugin not installed or not activated)"
            : $"{service} (provided by <{provider.Value.OwnerName}>, state: {provider.Value.OwnerState})";
    }

    /** 类型边缺失:列出全部可赋值候选及其提供者状态;零候选说明提供方未安装,多候选是歧义需按名消歧。 */
    private static string DescribeMissing(Context root, Type type)
    {
        var candidates = root.ServiceTable.DescribeCandidates(type);
        return candidates.Count == 0
            ? $"{type.Name} (no service assignable to {type.Name} registered)"
            : $"{type.Name} (ambiguous or inactive candidates: {string.Join("; ", candidates)})";
    }

    /** 单个插件 Apply 失败不拖垮整个进程:落 Failed 态并 WARN 汇总;入口插件的失败在入口解析处显式终止。 */
    private static void WarnFailed(Context root)
    {
        var failed = root.Scheduler.Snapshot()
            .Where(activation => activation.State == ActivationState.Failed)
            .ToList();
        if (failed.Count == 0)
            return;
        var logger = root.LoggerFor("composition");
        foreach (var activation in failed)
            logger.Warn($"plugin <{activation.Name}> failed to activate: {activation.Error}");
    }
}
