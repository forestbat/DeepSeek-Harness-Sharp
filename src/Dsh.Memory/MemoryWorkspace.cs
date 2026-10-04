using Dsh.Core;

namespace Dsh.Memory;

/** 按会话工作区解析项目记忆:一次进程内可同时服务多个项目,项目根相同则复用同一 ProjectMemory。 */
public sealed class MemoryWorkspace(MemoryPluginConfig config) : IProjectMemoryProvider, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ProjectMemory> _byRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _stores = [];

    public ProjectMemory For(string? cwd)
    {
        var root = ProjectRoot.Resolve(string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd);
        lock (_gate)
        {
            if (_byRoot.TryGetValue(root, out var existing))
                return existing;
            var store = MemoryStoreFactory.Create(config, root);
            if (store is IDisposable disposable)
                _stores.Add(disposable);
            var memory = new ProjectMemory(store, ProjectMemory.SidecarDirFor(root));
            _byRoot[root] = memory;
            return memory;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var store in _stores)
                store.Dispose();
            _stores.Clear();
            _byRoot.Clear();
        }
    }
}
