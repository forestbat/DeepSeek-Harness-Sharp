using System.Reflection;
using System.Runtime.Loader;

namespace Dsh.Plugins;

/** 共享程序集池:被声明为跨插件共享的依赖全进程只加载一份,保证类型同一性。
 *  解析顺序为 宿主镜像(Default) → 共享池 → 插件目录;池内程序集按引用计数,全零时整个池上下文可回收。 */
public sealed class SharedAssemblyPool
{
    private readonly string _directory;
    private readonly Dictionary<string, Assembly> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _references = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _notices = [];
    private SharedLoadContext _context;
    private bool _unloaded;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, VerifiedPluginBundle.ManagedFile> _verified = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VerifiedPluginBundle.NativeFile> _verifiedNative = new(StringComparer.Ordinal);

    public SharedAssemblyPool(string directory)
    {
        _directory = directory;
        _context = new SharedLoadContext(this, directory);
    }

    /** 供宿主把"提升到池""版本冲突"等事件接入日志。 */
    public IReadOnlyList<string> DrainNotices()
    {
        var notices = _notices.ToList();
        _notices.Clear();
        return notices;
    }

    /** 解析共享依赖:已在池中则做主版本一致性检查;不在则从 .shared 目录或插件自带副本(提升)装载。 */
    public Assembly? ResolveOrLoad(AssemblyName name, Func<string?> privatePath)
    {
        lock (_sync)
            return ResolveOrLoadCore(name, privatePath);
    }

    private Assembly? ResolveOrLoadCore(AssemblyName name, Func<string?> privatePath)
    {
        if (name.Name is null)
            return null;
        var existing = PluginLoadContext.ResolveHostAssembly(name) ?? _byName.GetValueOrDefault(name.Name);
        if (existing is not null)
        {
            if (name.Version is not null
                && existing.GetName().Version is { } have
                && have.Major != name.Version.Major)
            {
                throw new InvalidOperationException(
                    $"shared assembly {name.Name}: requested version {name.Version}, but the pool already has {have}; side-by-side loading is refused because split type identity is harder to debug than a load failure");
            }
            _byName.TryAdd(name.Name, existing);
            return existing;
        }
        if (_verified.TryGetValue(name.Name, out var verified))
            return LoadVerified(name.Name, verified);
        if (_unloaded)
        {
            _context = new SharedLoadContext(this, _directory);
            _unloaded = false;
        }
        var pooled = Path.Combine(_directory, name.Name + ".dll");
        var source = File.Exists(pooled) ? pooled : privatePath();
        if (source is null)
            return null;
        var assembly = _context.LoadFromAssemblyPath(source);
        _byName[name.Name] = assembly;
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(pooled), StringComparison.OrdinalIgnoreCase))
        {
            _notices.Add($"shared dependency {name.Name} was promoted from a plugin folder; "
                + $"move it to {pooled} and remove the per-plugin copies to deduplicate on disk");
        }
        return assembly;
    }

    /** The entire shared closure is checked before any plugin code is loaded. */
    internal void PrepareVerified(VerifiedPluginBundle bundle)
    {
        lock (_sync)
        {
            var closure = bundle.SharedClosure();
            var native = new Dictionary<string, VerifiedPluginBundle.NativeFile>(StringComparer.Ordinal);
            foreach (var import in closure.SelectMany(file => file.NativeImports).Distinct(StringComparer.Ordinal))
            {
                if (bundle.ResolveNativeFile(import) is not { } file)
                    continue;
                if (_verifiedNative.TryGetValue(import, out var previous) && previous.Digest != file.Digest)
                    throw new FileLoadException("A shared native dependency conflicts with the verified code bundle.");
                native.Add(import, file);
            }
            foreach (var file in closure)
            {
                var name = file.Name;
                if (PluginLoadContext.ResolveVerifiedHostAssembly(new AssemblyName(name)) is not null)
                    continue;
                var cached = _context.Assemblies.FirstOrDefault(assembly =>
                    string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
                if (_verified.TryGetValue(name, out var previous))
                {
                    if (!string.Equals(previous.Digest, file.Digest, StringComparison.OrdinalIgnoreCase))
                        throw new FileLoadException("A shared assembly conflicts with the verified code bundle.");
                }
                else if (cached is not null || _byName.ContainsKey(name)
                    || AssemblyLoadContext.Default.Assemblies.Any(assembly =>
                        string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw new FileLoadException("An unverified cached assembly cannot satisfy a verified shared dependency.");
            }
            foreach (var file in closure)
            {
                var name = file.Name;
                if (_verified.TryGetValue(name, out var previous))
                    bundle.UseShared(name, previous);
                else
                    _verified.Add(name, file);
            }
            foreach (var (name, file) in native)
                _verifiedNative.TryAdd(name, file);
            AddReferencesCore(bundle.SharedDependencies);
        }
    }

    internal Assembly ResolveVerified(AssemblyName name, VerifiedPluginBundle bundle)
    {
        lock (_sync)
        {
            if (name.Name is null || bundle.Find(name.Name) is not { } expected
                || !_verified.TryGetValue(name.Name, out var file) || file.Digest != expected.Digest)
                throw new FileLoadException("The shared dependency has no verified code identity.");
            return LoadVerified(name.Name, file);
        }
    }

    private Assembly LoadVerified(string name, VerifiedPluginBundle.ManagedFile file)
    {
        if (_byName.TryGetValue(name, out var assembly))
            return assembly;
        if (_unloaded)
        {
            _context = new SharedLoadContext(this, _directory);
            _unloaded = false;
        }
        assembly = file.Load(_context);
        _byName.Add(name, assembly);
        return assembly;
    }

    public void AddReferences(IEnumerable<string> names)
    {
        lock (_sync)
            AddReferencesCore(names);
    }

    private void AddReferencesCore(IEnumerable<string> names)
    {
        foreach (var name in names)
            _references[name] = _references.GetValueOrDefault(name) + 1;
    }

    /** 插件卸载时归还引用;全零即回收池上下文,回收失败由调用方按 leaked 语义上报。 */
    public void Release(IEnumerable<string> names)
    {
        lock (_sync)
            ReleaseCore(names);
    }

    private void ReleaseCore(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (!_references.TryGetValue(name, out var count))
                continue;
            if (count <= 1)
                _references.Remove(name);
            else
                _references[name] = count - 1;
        }
        if (_references.Count == 0 && (_byName.Count > 0 || _verified.Count > 0))
        {
            _byName.Clear();
            _verified.Clear();
            _verifiedNative.Clear();
            _context.Unload();
            _unloaded = true;
        }
    }

    /** Host identities precede private shared dependencies, including transitive references. */
    private sealed class SharedLoadContext(SharedAssemblyPool owner, string directory) : AssemblyLoadContext("dsh-shared", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            lock (owner._sync)
            {
                if (owner._verified.Count > 0)
                {
                    if (PluginLoadContext.ResolveVerifiedHostAssembly(assemblyName) is { } verifiedHost)
                        return verifiedHost;
                    if (assemblyName.Name is { } name && owner._verified.TryGetValue(name, out var file))
                        return owner.LoadVerified(name, file);
                    throw new FileNotFoundException("A transitive shared dependency is absent from the verified code bundle.");
                }
            }
            if (PluginLoadContext.ResolveHostAssembly(assemblyName) is { } host)
                return host;
            if (assemblyName.Name is not null)
            {
                var candidate = Path.Combine(directory, assemblyName.Name + ".dll");
                if (File.Exists(candidate))
                    return LoadFromAssemblyPath(candidate);
            }
            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            lock (owner._sync)
                if (owner._verifiedNative.TryGetValue(unmanagedDllName, out var file))
                    return LoadUnmanagedDllFromPath(file.Path);
            return IntPtr.Zero;
        }
    }
}
