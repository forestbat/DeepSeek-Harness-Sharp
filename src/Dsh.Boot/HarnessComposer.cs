using Dsh.Runtime;
using Dsh.Runtime.Composition;
using Dsh.Plugins;

namespace Dsh.Boot;

public sealed record HarnessOptions(
    HarnessHome Home,
    string? Cwd = null,
    string? Provider = null,
    string? Model = null,
    string? BaseUrl = null,
    string? ApiKeyEnv = null,
    string? ApiKey = null,
    string? ReasoningEffort = null,
    bool IsTui = false);

public sealed class HarnessApp : IDisposable
{
    public required Context Ctx { get; init; }
    public required HarnessHome Home { get; init; }
    public required ICredentials Credentials { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string? ReasoningEffort { get; init; }
    public Composition? Composition { get; set; }

    private readonly List<IDisposable> _disposables = [];

    internal void Track(IDisposable disposable) => _disposables.Add(disposable);

    /** 运行入口插件:按描述符 Entry 在已激活插件中解析,工厂实例须实现 IDshEntrypoint。
     *  只查激活者:被禁用/挂起的入口插件不占用入口名,替代插件才能接管。 */
    public async Task<int> RunEntrypointAsync(
        string name,
        PluginEntrypointOptions options,
        CancellationToken cancellationToken = default)
    {
        var catalog = Ctx.GetProp("pluginCatalog") as PluginCatalog
            ?? throw new RuntimeException("PLUGIN_CATALOG_MISSING", "the plugin catalog is not available");
        var active = Composition?.Activations
            .Where(activation => activation.State == ActivationState.Active)
            .Select(activation => activation.Name)
            .ToHashSet(StringComparer.Ordinal);
        bool IsRunnable(PluginDescriptor descriptor) => active is null || active.Contains(descriptor.Package);
        var descriptor = catalog.Descriptors.FirstOrDefault(entry => entry.Entry == name && IsRunnable(entry));
        if (descriptor is null)
        {
            var inactive = catalog.Descriptors.FirstOrDefault(entry => entry.Entry == name);
            if (inactive is not null)
                throw new RuntimeException("ENTRYPOINT_NOT_ACTIVE",
                    $"entrypoint '{name}' is provided by plugin '{inactive.Package}', which is not active (disabled, pending or failed)");
            var available = string.Join(", ", catalog.Descriptors
                .Where(entry => entry.Entry is not null && IsRunnable(entry))
                .Select(entry => entry.Entry));
            throw new RuntimeException("UNKNOWN_ENTRYPOINT",
                $"unknown plugin entrypoint '{name}' (available active entrypoints: {available})");
        }
        if (!catalog.TryGet(descriptor.Package, out var create) || create() is not IDshEntrypoint entrypoint)
        {
            throw new RuntimeException("ENTRYPOINT_NOT_RUNNABLE",
                $"plugin package '{descriptor.Package}' does not implement the entrypoint contract");
        }
        return await entrypoint.RunAsync(this, options, cancellationToken);
    }

    public void Dispose()
    {
        // 先让插件把效果放掉(编译进镜像的插件不走协作式卸载), 再拆基础设施; 否则文件句柄之类的资源会留到进程结束。
        try
        {
            Composition?.DeactivateAll();
        }
        catch (Exception error)
        {
            Ctx.LoggerFor("shutdown").Warn("%s", $"plugin deactivation failed: {error.Message}");
        }
        List<Exception>? errors = null;
        foreach (var disposable in ((IEnumerable<IDisposable>)_disposables).Reverse())
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception error)
            {
                // 任何一个关闭失败都不能阻断其余清理(例如日志文件句柄必须释放)。
                (errors ??= []).Add(error);
            }
        }
        if (errors is not null)
            throw new AggregateException("harness dispose failed", errors);
    }
}

public static class HarnessComposer
{
    public static async Task<HarnessApp> Compose(HarnessOptions options)
        => await ConfigBoot.Compose(options);
}

