using Dsh.Core;
using Dsh.Gui.ViewModels;
using Dsh.Runtime;

namespace Dsh.Gui.Services;

/** 侧栏数据源: 合并活跃 agent 与磁盘上的历史会话, 按 cwd 归并为工作区。 */
public sealed class SessionCatalog(Context ctx)
{
    public IReadOnlyList<SessionNodeViewModel> Load()
    {
        var nodes = new Dictionary<string, SessionNodeViewModel>(StringComparer.Ordinal);
        var persistence = ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, strict: false);
        foreach (var snapshot in persistence?.List() ?? [])
        {
            if (snapshot.Header.IsSubagent)
                continue;
            nodes[snapshot.Header.Id.Value] = Create(snapshot.Header, null);
        }

        foreach (var agent in LiveAgents())
        {
            if (agent.Session.Header.IsSubagent)
                continue;
            nodes[agent.Id.Value] = Create(agent.Session.Header, agent);
        }

        return [.. nodes.Values.OrderByDescending(node => node.CreatedAt)];
    }

    private IReadOnlyList<AgentLoopAgent> LiveAgents()
        => ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)?.List().OfType<AgentLoopAgent>().ToList() ?? [];

    private static SessionNodeViewModel Create(SessionHeader header, AgentLoopAgent? agent)
    {
        var node = new SessionNodeViewModel
        {
            SessionId = header.Id,
            Workspace = WorkspaceName(header.Cwd),
            Cwd = header.Cwd ?? "",
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(header.CreatedAt),
        };
        node.Refresh(agent, header.Title ?? "", agent is null ? "" : $"{agent.Options.Provider}/{agent.Options.Model}");
        return node;
    }

    private static string WorkspaceName(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
            return "未指定目录";
        var trimmed = cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }
}
