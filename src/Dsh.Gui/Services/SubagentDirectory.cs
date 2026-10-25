using Dsh.Core;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Gui.Services;

/** Read-only subagent queries are served by the same Host that owns the parent chat. */
public sealed class SubagentDirectory(HostChatSession chat)
{
    private readonly Dictionary<SessionId, IReadOnlyList<SessionEvent>> _events = [];

    public async Task<IReadOnlyList<RemoteSubagentInfo>> DescendantsAsync(SessionId root)
    {
        if (!chat.IsHarness)
            return [];
        var entries = await (await chat.ClientAsync()).SessionEditing.ListSubagentsAsync(root.Value);
        // Keep the existing tree presentation: creation-ordered siblings, depth-first preorder.
        var byParent = entries.Where(entry => entry.Parent is not null)
            .GroupBy(entry => entry.Parent!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(entry => entry.CreatedAt)
                .ThenBy(entry => entry.SessionId, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var ordered = new List<RemoteSubagentInfo>(entries.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal) { root.Value };
        AddChildren(root.Value);
        return ordered;

        void AddChildren(string parent)
        {
            if (!byParent.TryGetValue(parent, out var children))
                return;
            foreach (var child in children)
            {
                if (!visited.Add(child.SessionId))
                    continue;
                ordered.Add(child);
                AddChildren(child.SessionId);
            }
        }
    }

    public IReadOnlyList<SessionEvent>? FindEvents(SessionId id)
        => _events.GetValueOrDefault(id);

    public async Task<IReadOnlyList<SessionEvent>> LoadEventsAsync(SessionId id)
    {
        var snapshot = await (await chat.ClientAsync()).GetSessionSnapshotAsync(id.Value);
        var events = snapshot.Events.ToList();
        _events[id] = events;
        return events;
    }
}
