using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Subagent;

namespace Dsh.Gui.Services;

/** GUI 侧子代理查询: 直接消费进程内 SubagentRuntime 的持久化子树与 SessionStore, 不引入新协议; 服务缺失时退化为空。 */
public sealed class SubagentDirectory
{
    private readonly SubagentRuntime? _runtime;
    private readonly SessionStore? _store;

    public SubagentDirectory(Context ctx)
    {
        _runtime = ctx.Get<SubagentRuntime>(SubagentRuntime.ServiceName, false);
        _store = ctx.Get<SessionStore>(SessionStore.ServiceName, false);
    }

    public IReadOnlyList<SubagentListEntry.Child> Descendants(SessionId root)
    {
        if (_runtime is null)
            return [];
        try
        {
            var flat = _runtime.ListDescendantsAsync(root).GetAwaiter().GetResult().OfType<SubagentListEntry.Child>().ToList();
            return OrderByCreation(root, flat);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /** 后端平铺顺序会把同层兄弟先全部列出再下钻, 这里按会话创建顺序重排为稳定的前序树。 */
    private IReadOnlyList<SubagentListEntry.Child> OrderByCreation(SessionId root, IReadOnlyList<SubagentListEntry.Child> entries)
    {
        var byParent = new Dictionary<SessionId, List<SubagentListEntry.Child>>();
        foreach (var entry in entries)
        {
            if (entry.Parent is not { } parent)
                continue;
            if (!byParent.TryGetValue(parent, out var siblings))
                byParent[parent] = siblings = [];
            siblings.Add(entry);
        }
        var ordered = new List<SubagentListEntry.Child>();
        var visited = new HashSet<SessionId> { root };
        AddChildren(root);
        return ordered;

        void AddChildren(SessionId parentId)
        {
            if (!byParent.TryGetValue(parentId, out var siblings))
                return;
            foreach (var child in siblings
                         .OrderBy(entry => CreationTime(entry.Id))
                         .ThenBy(entry => entry.Id.Value, StringComparer.Ordinal))
            {
                if (!visited.Add(child.Id))
                    continue;
                ordered.Add(child);
                AddChildren(child.Id);
            }
        }
    }

    private long CreationTime(SessionId id) => _store?.Get(id)?.Header.CreatedAt ?? 0;

    public bool IsLive(SessionId id) => _runtime?.GetLive(id) is not null;

    public Session? FindSession(SessionId id) => _runtime?.GetLive(id)?.Session ?? _store?.Get(id);
}
