using Dsh.Core;
using Dsh.Gui.ViewModels;
using Dsh.Runtime;

namespace Dsh.Gui.Services;

/** 侧栏数据源: 合并活跃 agent 与磁盘上的历史会话, 按 cwd 归并为工作区。 */
public sealed class SessionCatalog(Context ctx)
{
    /** 空 cwd 会话的占位分组名。 */
    public const string UnspecifiedWorkspace = "未指定目录";

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
        var workspacePath = WorkspacePath(header.Cwd);
        var node = new SessionNodeViewModel
        {
            SessionId = header.Id,
            Workspace = WorkspaceDisplayName(workspacePath),
            WorkspacePath = workspacePath,
            Cwd = header.Cwd ?? "",
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(header.CreatedAt),
        };
        node.Refresh(agent, header.Title ?? "", agent is null ? "" : $"{agent.Options.Provider}/{agent.Options.Model}");
        return node;
    }

    /** 分组键: 归一化后的完整路径; 空 cwd 归到一个占位组。 */
    public static string WorkspacePath(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
            return UnspecifiedWorkspace;
        try
        {
            return Path.GetFullPath(cwd.Trim());
        }
        catch (Exception)
        {
            return cwd.Trim();
        }
    }

    /** 显示名: 末段目录名; 占位分组原样返回。 */
    public static string WorkspaceDisplayName(string path)
    {
        if (path == UnspecifiedWorkspace)
            return path;
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }
}
