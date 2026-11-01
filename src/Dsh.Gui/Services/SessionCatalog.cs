using Dsh.Core;
using Dsh.Gui.ViewModels;
using Dsh.Workspace;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Runtime;

namespace Dsh.Gui.Services;

/** Sidebar history merges persisted pre-Fleet sessions with the local Host registry; only Host writes sessions. */
public sealed class SessionCatalog(Context ctx, FleetWorkspace fleet)
{
    /** 空 cwd 会话的占位分组名。 */
    public const string UnspecifiedWorkspace = "未指定目录";

    /** 磁盘扫描结果; null 表示尚未扫描。见 DiskSnapshots 的说明。 */
    private IReadOnlyList<SessionPersistenceSnapshot>? _disk;

    public IReadOnlyList<SessionNodeViewModel> Load()
    {
        var nodes = new Dictionary<string, SessionNodeViewModel>(StringComparer.Ordinal);
        foreach (var snapshot in DiskSnapshots())
        {
            if (snapshot.Header.IsSubagent)
                continue;
            nodes[snapshot.Header.Id.Value] = Create(snapshot.Header);
        }

        foreach (var session in fleet.Find(MachineSpec.LocalName)?.Sessions ?? [])
        {
            if (session.Kind != HostSessionKinds.Harness)
                continue;
            var path = WorkspacePath(session.ProjectPath);
            nodes[session.SessionId] = new SessionNodeViewModel
            {
                SessionId = SessionId.Create(session.SessionId),
                Workspace = WorkspaceDisplayName(path),
                WorkspacePath = path,
                Cwd = session.ProjectPath ?? "",
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(session.CreatedAt),
                Title = session.Title ?? session.SessionId,
                IsLive = session.Status != HostSessionStatuses.Exited,
                IsRunning = session.Status == HostSessionStatuses.Running,
                RelativeTime = RelativeTimeText.Format(DateTimeOffset.FromUnixTimeMilliseconds(session.LastActivityAt)),
            };
        }

        return [.. nodes.Values.OrderByDescending(node => node.CreatedAt)];
    }

    /**
     * 磁盘上的历史会话, 扫描一次后缓存。
     *
     * 它只用来补齐 Fleet 里没有的会话 —— Fleet 是 Host 写会话的权威来源, 同一个 id 会被 Load 里的循环覆盖。
     * 而一次全量扫描要读每个日志的 zstd 首行(实测侧栏每刷新一次 ~95ms, 全部落在 UI 线程上),
     * 所以不能每轮刷新都重扫。会话被删除时由调用方 InvalidateDiskCache 丢弃缓存。
     */
    private IReadOnlyList<SessionPersistenceSnapshot> DiskSnapshots()
    {
        if (_disk is { } cached)
            return cached;
        var persistence = ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, strict: false);
        // 服务尚未注册时不缓存: 空结果一旦缓存, 之后即使服务就绪也永远看不到历史会话。
        return persistence is null ? [] : _disk = persistence.List();
    }

    /** 丢弃磁盘缓存, 下次 Load 重新扫描。会话被删除时调用。 */
    public void InvalidateDiskCache() => _disk = null;


    private static SessionNodeViewModel Create(SessionHeader header)
    {
        var workspacePath = WorkspacePath(header.Cwd);
        var node = new SessionNodeViewModel
        {
            SessionId = header.Id,
            Workspace = WorkspaceDisplayName(workspacePath),
            WorkspacePath = workspacePath,
            Cwd = header.Cwd ?? "",
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(header.CreatedAt),
            Title = header.Title ?? header.Id.Value,
            RelativeTime = RelativeTimeText.Format(DateTimeOffset.FromUnixTimeMilliseconds(header.CreatedAt)),
        };
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
