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

    public PluginLoadContext(string pluginPath, string? name = null)
        : base(name ?? $"dsh-plugin:{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
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
        // 宿主镜像(框架与 compiled-in 模块)自带即绑定 Default:返回 null 交给默认回退。
        if (TryHostResolve(assemblyName))
            return null;
        if (_pool is not null && _sharedNames.Contains(name))
            return _pool.ResolveOrLoad(assemblyName, () => _resolver.ResolveAssemblyToPath(assemblyName));
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    private static bool TryHostResolve(AssemblyName assemblyName)
    {
        try
        {
            return Default.LoadFromAssemblyName(assemblyName) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
