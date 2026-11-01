using System.Reflection;
using System.Runtime.Loader;

namespace Dsh.Plugins;

/** 可回收插件加载上下文:按插件路径解析依赖,支持 Unload() 后回收。
 *  解析顺序按事实而非前缀:宿主镜像(Default 可解析) → 声明共享的进共享池 → 插件目录私有副本。 */
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private SharedAssemblyPool? _pool;
    private HashSet<string> _sharedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly VerifiedPluginBundle? _bundle;
    private static readonly IReadOnlyDictionary<string, string> HostPaths =
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => Path.GetFullPath(group.First()), StringComparer.OrdinalIgnoreCase);

    public PluginLoadContext(string pluginPath, string? name = null)
        : base(name ?? $"dsh-plugin:{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    internal PluginLoadContext(VerifiedPluginBundle bundle, SharedAssemblyPool pool) : this(bundle.EntryPath)
    {
        _bundle = bundle;
        ConfigureShared(pool, bundle.SharedDependencies);
    }

    /** 清单读出后调用:声明共享的依赖名进共享池解析,其余维持目录私有。 */
    internal void ConfigureShared(SharedAssemblyPool? pool, IEnumerable<string> sharedNames)
    {
        _pool = pool;
        _sharedNames = new HashSet<string>(sharedNames, StringComparer.OrdinalIgnoreCase);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null)
            return null;
        if (_bundle is not null)
        {
            if (ResolveVerifiedHostAssembly(assemblyName) is { } verifiedHost)
                return verifiedHost;
            if (_sharedNames.Contains(name))
                return _pool!.ResolveVerified(assemblyName, _bundle);
            return _bundle.Find(name)?.Load(this)
                ?? throw new FileNotFoundException("A managed dependency is absent from the verified code bundle.");
        }
        // Host contracts, including Protocol and Sandbox, keep their Default identity.
        if (ResolveHostAssembly(assemblyName) is { } host)
            return host;
        if (_pool is not null && _sharedNames.Contains(name))
            return _pool.ResolveOrLoad(assemblyName, () => _resolver.ResolveAssemblyToPath(assemblyName));
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (_bundle is not null)
        {
            path = _bundle.ResolveNativeFile(unmanagedDllName)?.Path ?? _bundle.DemandNativePath(path);
        }
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    internal static Assembly? ResolveHostAssembly(AssemblyName assemblyName)
    {
        try
        {
            return Default.LoadFromAssemblyName(assemblyName);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    internal static Assembly? ResolveVerifiedHostAssembly(AssemblyName name)
    {
        if (name.Name is null || !HostPaths.TryGetValue(name.Name, out var path))
            return null;
        var existing = Default.Assemblies.FirstOrDefault(assembly =>
            string.Equals(assembly.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
        var assembly = existing ?? Default.LoadFromAssemblyPath(path);
        if (assembly.IsDynamic || !string.Equals(Path.GetFullPath(assembly.Location), path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new FileLoadException("An unverified assembly occupies a host runtime identity.");
        return assembly;
    }
}
