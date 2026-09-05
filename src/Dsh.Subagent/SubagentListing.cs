using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Subagent;

public abstract record SubagentListEntry
{
    public required SessionId Id { get; init; }
    public SessionId? Parent { get; init; }
    public int? Depth { get; init; }

    public sealed record Child : SubagentListEntry
    {
        public required string Mode { get; init; }
        public string? Label { get; init; }
        public bool HasChildren { get; init; }
    }

    public sealed record Diagnostic : SubagentListEntry
    {
        public required string Reason { get; init; }
    }
}

public sealed partial class SubagentRuntime
{
    public Task<IReadOnlyList<SubagentListEntry>> ListChildrenAsync(SessionId parentSessionId, CancellationToken signal = default)
    {
        var sessions = SessionsOrThrow();
        signal.ThrowIfCancellationRequested();
        var live = sessions.List();
        var subagentParents = SubagentParents(live);
        var rows = new List<SubagentListEntry>();
        foreach (var candidate in OrderedSubagentChildren(live, parentSessionId))
        {
            signal.ThrowIfCancellationRequested();
            if (SubagentDescriptorPayload.IdentityOf(candidate) is not { } identity)
                continue;
            rows.Add(new SubagentListEntry.Child
            {
                Id = candidate.Id,
                Mode = identity.Mode,
                Label = identity.Label,
                HasChildren = subagentParents.Contains(candidate.Id),
            });
        }
        return Task.FromResult<IReadOnlyList<SubagentListEntry>>(rows);
    }

    public Task<IReadOnlyList<SubagentListEntry>> ListDescendantsAsync(
        SessionId parentSessionId, int depth = int.MaxValue, CancellationToken signal = default)
    {
        if (depth < 1)
            throw new ArgumentException("subagent listDescendants depth must be a positive safe integer");
        var sessions = SessionsOrThrow();
        signal.ThrowIfCancellationRequested();
        var live = sessions.List();
        var subagentParents = SubagentParents(live);
        var children = new Dictionary<SessionId, List<Session>>();
        foreach (var session in live)
        {
            if (session.Header is not { Origin: "subagent", ParentSession: { } parent }
                || SubagentDescriptorPayload.IdentityOf(session) is null)
            {
                continue;
            }
            if (!children.TryGetValue(parent, out var list))
                children[parent] = list = [];
            list.Add(session);
        }
        foreach (var list in children.Values)
            list.Sort(CompareByCreation);
        var positioned = WalkDescendants(children, parentSessionId, depth, signal);
        var rows = positioned
            .Select(item => ToEntry(item.Session, subagentParents, item.Parent, item.Depth))
            .ToList();
        return Task.FromResult<IReadOnlyList<SubagentListEntry>>(rows);
    }

    private SessionStore SessionsOrThrow()
        => Ctx.Get<SessionStore>(SessionStore.ServiceName)
            ?? throw new SubagentException(
                "listing subagents requires the session store (load @deepseek-ai/dsh-session)",
                SubagentErrorCodes.SessionStoreUnavailable);

    private static HashSet<SessionId> SubagentParents(IReadOnlyList<Session> live)
    {
        var parents = new HashSet<SessionId>();
        foreach (var session in live)
        {
            if (session.Header is { Origin: "subagent", ParentSession: { } parent })
                parents.Add(parent);
        }
        return parents;
    }

    private static IEnumerable<Session> OrderedSubagentChildren(IReadOnlyList<Session> live, SessionId parentSessionId)
        => live
            .Where(session => session.Header.ParentSession == parentSessionId && session.Header.Origin == "subagent")
            .OrderBy(session => session.Header.CreatedAt)
            .ThenBy(session => session.Header.Id.Value, StringComparer.Ordinal);

    private static List<(Session Session, SessionId Parent, int Depth)> WalkDescendants(
        Dictionary<SessionId, List<Session>> children, SessionId root, int depth, CancellationToken signal)
    {
        var positioned = new List<(Session, SessionId, int)>();
        var visited = new HashSet<SessionId> { root };
        var stack = new Stack<(SessionId Id, int Level)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            signal.ThrowIfCancellationRequested();
            var (current, level) = stack.Pop();
            if (level >= depth || !children.TryGetValue(current, out var list))
                continue;
            for (var index = list.Count - 1; index >= 0; index--)
            {
                var child = list[index];
                if (!visited.Add(child.Id))
                    continue;
                positioned.Add((child, current, level + 1));
                stack.Push((child.Id, level + 1));
            }
        }
        return positioned;
    }

    private static SubagentListEntry ToEntry(Session session, HashSet<SessionId> subagentParents, SessionId parent, int depth)
    {
        var identity = SubagentDescriptorPayload.IdentityOf(session)!;
        return new SubagentListEntry.Child
        {
            Id = session.Id,
            Mode = identity.Mode,
            Label = identity.Label,
            HasChildren = subagentParents.Contains(session.Id),
            Parent = parent,
            Depth = depth,
        };
    }

    private static int CompareByCreation(Session left, Session right)
    {
        var byTime = left.Header.CreatedAt.CompareTo(right.Header.CreatedAt);
        return byTime != 0 ? byTime : string.CompareOrdinal(left.Header.Id.Value, right.Header.Id.Value);
    }
}
