using System.Runtime.CompilerServices;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Runtime.Composition;

namespace Dsh.Boot;

public sealed class HarnessPluginManager : IPluginManager, IDisposable
{
    private readonly PluginHost _host;
    private readonly Composition _composition;
    private readonly HarnessHome _home;
    private readonly HarnessSettings _settings;
    private readonly string _pluginsDirectory;
    private readonly Dictionary<string, PluginLoadContext> _loadedContexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _sharedDependencies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _installedDirectories = new(StringComparer.Ordinal);
    private readonly HashSet<string> _leaked = new(StringComparer.Ordinal);

    public HarnessPluginManager(
        PluginHost host,
        Composition composition,
        HarnessHome home,
        HarnessSettings settings,
        IReadOnlyList<ManagedPlugin> loadedOnStart,
        string pluginsDirectory)
    {
        _host = host;
        _composition = composition;
        _home = home;
        _settings = settings;
        _pluginsDirectory = pluginsDirectory;
        foreach (var entry in loadedOnStart)
        {
            _loadedContexts[entry.Package] = entry.Context;
            if (entry.SharedDependencies.Count > 0)
                _sharedDependencies[entry.Package] = entry.SharedDependencies;
            if (entry.InstallDirectory is { } directory)
                _installedDirectories[entry.Package] = directory;
        }
    }

    public IReadOnlyList<string> PackageNames => _host.Catalog.PackageNames
        .Distinct()
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToList();

    public string Describe(string package)
    {
        var activation = _composition.Find(package);
        var state = activation?.State switch
        {
            ActivationState.Active => "active",
            ActivationState.Activating => "activating",
            ActivationState.Deactivating => "deactivating (unloading)",
            ActivationState.Failed => $"failed: {activation.Error}",
            ActivationState.Disposed => "removed",
            ActivationState.Pending => "pending (dependencies missing)",
            null when _settings.Plugins.TryGetValue(package, out var setting) && !setting.Enabled => "disabled",
            _ => "available",
        };
        var descriptor = Descriptor(package);
        var line = descriptor is null ? state : $"{state} [{FormName(descriptor)}]";
        return _leaked.Contains(package) ? $"{line}, leaked" : line;
    }

    public async Task<string> AddAsync(string packageOrPath)
    {
        var spec = packageOrPath.Trim();
        if (spec.Length == 0)
            return "usage: /plugins add <package|path>";
        if (spec.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || File.Exists(spec))
            return await AddFromPathAsync(spec);
        if (_host.Catalog.PackageNames.Contains(spec, StringComparer.Ordinal))
            return await ActivateAsync(spec);
        return $"plugin package not found: {spec}";
    }

    public async Task<string> RemoveAsync(string package, bool force = false)
    {
        var trimmed = package.Trim();
        if (trimmed.Length == 0)
            return "usage: /plugins remove <package>";
        var active = _composition.Root.Scheduler.Find(trimmed) is not null;
        if (!active && !_installedDirectories.ContainsKey(trimmed))
            return $"plugin {trimmed} is not active";
        var message = active ? await DetachAsync(trimmed, force) : Removed(trimmed, leaked: false);
        var uninstall = await UninstallInstalledAsync(trimmed);
        return uninstall is null ? message : $"{message}; {uninstall}";
    }

    /** 摘除并登记为禁用;制品目录的删除交给 remove 的 UninstallInstalled。 */
    private async Task<string> DetachAsync(string package, bool force)
    {
        var (weak, leaked) = await UnloadAsync(package, force, reclaim: IsManaged(package));
        Persist(package, new PluginSetting { Enabled = false });
        if (leaked)
            _leaked.Add(package);
        if (weak is null)
            return Removed(package, leaked);
        _ = VerifyCollectionAsync(package, weak);
        return Removed(package, leaked) + "(加载上下文回收校验异步进行中;若泄漏会记录日志并在 /plugins list 标记 leaked)";
    }

    /** 删除 add 落盘的每插件目录;未跟踪到目录(compiled-in / 平铺布局)则返回 null。 */
    private async Task<string?> UninstallInstalledAsync(string package)
    {
        if (!_installedDirectories.Remove(package, out var directory))
            return null;
        // Windows 上被装箱的程序集文件在 ALC 回收前仍被锁定:催促 GC 后有界重试。
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                PluginInstall.Uninstall(directory, _pluginsDirectory);
                return $"已删除安装目录 {directory}";
            }
            catch (Exception error) when (attempt < 60 && error is IOException or UnauthorizedAccessException)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                await Task.Delay(50);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _installedDirectories[package] = directory;
                return $"删除安装目录失败: {directory}: {error.Message}";
            }
        }
    }

    public async Task<string> DisableAsync(string package)
    {
        var trimmed = package.Trim();
        if (trimmed.Length == 0)
            return "usage: /plugins disable <package>";
        if (!IsKnown(trimmed))
            return $"plugin package not found: {trimmed}";
        if (_composition.Root.Scheduler.Find(trimmed) is not null)
        {
            // 禁用只卸载效果,不回收制品:登记仍在,enable 可复原;回收交给 remove。
            var outcome = await UnloadAsync(trimmed, force: false, reclaim: false);
            if (outcome.Leaked)
            {
                _leaked.Add(trimmed);
                return $"plugin {trimmed} disabled with timeout; effects may still be running (leaked)";
            }
        }
        Persist(trimmed, new PluginSetting { Enabled = false });
        return $"plugin {trimmed} disabled (removed from settings.yaml)";
    }

    public async Task<string> EnableAsync(string package)
    {
        var trimmed = package.Trim();
        if (trimmed.Length == 0)
            return "usage: /plugins enable <package>";
        if (!IsKnown(trimmed))
            return $"plugin package not found: {trimmed}";
        Persist(trimmed, new PluginSetting { Enabled = true });
        if (_composition.Root.Scheduler.Find(trimmed) is not null)
            return $"plugin {trimmed} enabled (already active)";
        return await ActivateAsync(trimmed);
    }

    private async Task<string> AddFromPathAsync(string spec)
    {
        var path = Path.GetFullPath(spec);
        if (!File.Exists(path))
            return $"plugin file not found: {path}";
        if (!RuntimeFeature.IsDynamicCodeSupported)
            return "NativeAOT build cannot load managed plugins at runtime; compile the plugin into the image or ship it as a native library";
        // 先落盘再装载:plugins/<程序集名>/ 目录是重启后的发现入口,只装进程不拷贝会导致重启即丢。
        string installPath;
        try
        {
            installPath = PluginInstall.Install(path, _pluginsDirectory);
        }
        catch (InvalidOperationException error)
        {
            return $"plugin install failed: {error.Message}";
        }
        PluginLoadResult result;
        try
        {
            result = _host.TryLoad(installPath);
        }
        catch (Exception error) when (error is BadImageFormatException or FileLoadException)
        {
            return $"assembly load failed: {installPath}: {error.Message}";
        }
        if (result.Packages.Count == 0)
        {
            var reasons = string.Join("; ", result.Skipped.Select(skip => skip.Reason));
            return reasons.Length == 0 ? $"assembly has no DSH plugin: {installPath}" : $"plugin load skipped: {reasons}";
        }
        var messages = new List<string>();
        foreach (var package in result.Packages)
        {
            _loadedContexts[package] = result.Context!;
            _installedDirectories[package] = Path.GetDirectoryName(installPath)!;
            if (result.SharedDependencies.Count > 0)
                _sharedDependencies[package] = result.SharedDependencies;
            messages.Add(await ActivateAsync(package));
        }
        foreach (var notice in _host.SharedPool?.DrainNotices() ?? [])
            messages.Add($"note: {notice}");
        return string.Join('\n', messages);
    }

    private async Task<string> ActivateAsync(string package)
    {
        if (_composition.Find(package) is { } existing)
            return $"plugin {package} is already active ({existing.State})";
        if (!_host.Catalog.TryCreateDefinition(package, out var definition))
            return $"plugin package not found: {package}";
        var activation = await _composition.AddAsync(definition!);
        if (activation.State == ActivationState.Active)
        {
            Persist(package, new PluginSetting { Enabled = true });
            return $"plugin {package} activated";
        }
        return $"plugin {package} failed to activate: {activation.Error}";
    }

    /** 摘除插件并返回弱引用供回收验证;reclaim 仅对托管程序集形态成立(可回收 ALC)。
     *  独立成帧并只返回弱引用,使插件类型/定义等强引用在本方法返回后即可回收。 */
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<(WeakReference? Weak, bool Leaked)> UnloadAsync(string package, bool force, bool reclaim)
    {
        var activation = await _composition.Root.Scheduler.UnloadAsync(package, force: force);
        var leaked = activation is null || activation.State != ActivationState.Disposed;
        Release(ref activation);
        await _composition.Root.Scheduler.SettleAsync();   // 排空调度泵,避免快照任务滞留插件引用
        if (!reclaim || !_loadedContexts.Remove(package, out var context))
            return (null, leaked);
        _host.Catalog.Remove(package);
        if (_sharedDependencies.Remove(package, out var shared))
            _host.SharedPool?.Release(shared);
        var weak = PluginUnloader.Unload(context);
        Release(ref context);
        return (weak, leaked);
    }

    /** 置空引用以断开 插件类型→程序集→ALC 的强引用,供后续回收校验。 */
    private static void Release<T>(ref T? value) where T : class
    {
        _ = value;
        value = null;
    }

    private bool IsKnown(string package)
        => _composition.Root.Scheduler.Find(package) is not null
            || _host.Catalog.PackageNames.Contains(package, StringComparer.Ordinal);

    private bool IsManaged(string package)
        => Descriptor(package)?.Form == PluginForm.ManagedAssembly;

    private PluginDescriptor? Descriptor(string package)
        => _host.Catalog.TryDescribe(package, out var descriptor) ? descriptor : null;

    private static string FormName(PluginDescriptor descriptor)
    {
        var form = descriptor.Form switch
        {
            PluginForm.CompiledIn => "compiled-in",
            PluginForm.ManagedAssembly => "managed-assembly",
            _ => "native-library",
        };
        return descriptor.Capabilities.HasFlag(PluginCapabilities.Unload) ? $"{form}, reclaimable" : form;
    }

    /** 分离帧验证 ALC 回收:调用帧的残留引用(异步状态机/局部变量)会误判,故放到独立任务里做。 */
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task VerifyCollectionAsync(string package, WeakReference weak)
    {
        await Task.Yield();
        if (PluginUnloader.WaitForCollection(weak, out var report))
        {
            _leaked.Remove(package);
            return;
        }
        _leaked.Add(package);
        _composition.Root.Logger.Error("%s",
            $"plugin {package} 的加载上下文未能回收:{report};静态引用或未退出的线程会阻止回收,"
            + "可用 `dotnet-dump analyze <pid>` 执行 `!dumpheap -type LoaderAllocator` 与 `!gcroot <addr>` 排查");
    }

    private void Persist(string package, PluginSetting setting)
    {
        _settings.Plugins[package] = setting;
        _settings.SavePlugins(_home);
    }

    public void Dispose()
    {
        foreach (var context in _loadedContexts.Values.Distinct())
        {
            if (context.IsCollectible)
                context.Unload();
        }
        _loadedContexts.Clear();
    }

    private static string Removed(string package, bool leaked)
        => leaked
            ? $"plugin {package} removed, but settlement timed out (effects may still be running; marked leaked)"
            : $"plugin {package} removed";
}
