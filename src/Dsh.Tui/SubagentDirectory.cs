using Dsh.Runtime;
using Dsh.Llm;
using Dsh.Core;
using Dsh.Subagent;

namespace Dsh.Tui;

/** 子代理树条目: Id/Label/Mode/层级 来自持久化 descriptor, live 由通知与 GetLive 共同判定。 */
internal sealed record SubagentNode(
    SessionId Id,
    SessionId? Parent,
    int Depth,
    string Mode,
    string? Label,
    bool HasChildren);

/**
 * TUI 侧子代理查询层: 直接消费进程内 SubagentRuntime 的子树/活代理查询与 SessionStore,
 * 不引入新协议; 服务缺失时全部退化为空结果。
 */
internal sealed class SubagentDirectory
{
    private readonly SubagentRuntime? _runtime;
    private readonly SessionStore? _store;

    public SubagentDirectory(Context ctx)
    {
        _runtime = ctx.Get<SubagentRuntime>(SubagentRuntime.ServiceName, false);
        _store = ctx.Get<SessionStore>(SessionStore.ServiceName, false);
    }

    public IReadOnlyList<SubagentNode> Descendants(SessionId root)
    {
        var nodes = QueryDescendants(root);
        if (nodes.Count == 0)
            return nodes;

        // 后端返回的平铺顺序会把同层兄弟先全部列出再下钻; 这里按会话创建顺序重排为稳定的前序树。
        var byParent = new Dictionary<SessionId, List<SubagentNode>>();
        foreach (var node in nodes)
        {
            if (node.Parent is not { } parent)
                continue;
            if (!byParent.TryGetValue(parent, out var children))
                byParent[parent] = children = [];
            children.Add(node);
        }

        var ordered = new List<SubagentNode>();
        var visited = new HashSet<SessionId> { root };
        AddChildren(root, 1);
        return ordered;

        void AddChildren(SessionId parentId, int depth)
        {
            if (!byParent.TryGetValue(parentId, out var children))
                return;
            foreach (var child in children
                         .OrderBy(node => CreationTime(node.Id))
                         .ThenBy(node => node.Id.Value, StringComparer.Ordinal))
            {
                if (!visited.Add(child.Id))
                    continue;
                ordered.Add(child with { Parent = parentId, Depth = depth });
                AddChildren(child.Id, depth + 1);
            }
        }
    }

    private IReadOnlyList<SubagentNode> QueryDescendants(SessionId root)
    {
        if (_runtime is null)
            return [];
        try
        {
            return _runtime.ListDescendantsAsync(root).GetAwaiter().GetResult()
                .OfType<SubagentListEntry.Child>()
                .Select(child => new SubagentNode(
                    child.Id,
                    child.Parent,
                    child.Depth ?? 0,
                    child.Mode,
                    child.Label,
                    child.HasChildren))
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public IReadOnlyList<SubagentNode> Children(SessionId parent)
    {
        if (_runtime is null)
            return [];
        try
        {
            return _runtime.ListChildrenAsync(parent).GetAwaiter().GetResult()
                .OfType<SubagentListEntry.Child>()
                .Select(child => new SubagentNode(
                    child.Id,
                    parent,
                    DepthOf(child.Id),
                    child.Mode,
                    child.Label,
                    child.HasChildren))
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    public SubagentNode? FindChildByLabel(SessionId parent, string label)
        => Children(parent).FirstOrDefault(child => string.Equals(child.Label, label, StringComparison.Ordinal));

    public Session? FindSession(SessionId id)
        => _runtime?.GetLive(id)?.Session ?? _store?.Get(id);

    public bool IsLive(SessionId id) => _runtime?.GetLive(id) is not null;

    private int DepthOf(SessionId id) => _store?.Get(id)?.Header.DelegationDepth ?? 0;

    private long CreationTime(SessionId id) => _store?.Get(id)?.Header.CreatedAt ?? 0;
}
