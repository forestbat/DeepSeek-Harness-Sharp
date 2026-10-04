using System.Text.Json;

namespace Dsh.Pty;

public sealed class PtyDaemonNotRunningException : Exception
{
    public PtyDaemonNotRunningException()
        : base("daemon not running")
    {
    }
}

public static class PtyDaemonPaths
{
    public static string DefaultRoot()
    {
        var home = Environment.GetEnvironmentVariable("DSH_HOME");
        if (!string.IsNullOrWhiteSpace(home))
            return home;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
    }

    public static string SocketPath(string? root = null)
        => Path.Combine(root ?? DefaultRoot(), "run", "dsh.sock");

    public static string PortFile(string? root = null)
        => Path.Combine(root ?? DefaultRoot(), "run", "dsh.port");

    public static string RunDirectory(string? root = null)
        => Path.GetDirectoryName(SocketPath(root))!;

    /**
     * 会话尺寸文件: ConPTY 的子进程在 ResizePseudoConsole 之后读不到新的窗口尺寸,
     * 所以 daemon(尺寸的权威方, 与 tmux 的 server 同角色)把当前尺寸落盘, 常驻 TUI 自己读。
     */
    public static string SessionSizeFile(string sessionId, string? root = null)
        => Path.Combine(RunDirectory(root), $"size-{sessionId}");
}

public sealed class PtyDaemonRequest
{
    public string Method { get; set; } = "";

    public string? Id { get; set; }

    public PtyDaemonStartParams? Params { get; set; }

    /** method=mouse: 与 Win32 MOUSE_EVENT_RECORD 一一对应, daemon 原样注入常驻会话的控制台输入缓冲。 */
    public short MouseX { get; set; }

    public short MouseY { get; set; }

    public uint MouseButtonState { get; set; }

    public uint MouseEventFlags { get; set; }

    /** method=identify/publish-panes: 常驻 TUI 上报自己的 agent 会话 id。 */
    public string? AgentSessionId { get; set; }

    /** method=publish-panes: 常驻 TUI 发布的窗格目录与尾行。 */
    public List<PtyPaneSnapshotDto>? Panes { get; set; }

    /** method=control-send: 目标窗格与输入内容。 */
    public int PaneId { get; set; }

    public string? Kind { get; set; }

    public string? Payload { get; set; }

    /** method=control-read: 只取该 seq 之后的新控制消息。 */
    public long SinceSeq { get; set; }
}

public sealed class PtyDaemonStartParams
{
    public string? Id { get; set; }

    public string FileName { get; set; } = "";

    public List<string> Arguments { get; set; } = [];

    public string? WorkingDirectory { get; set; }

    public Dictionary<string, string?>? Environment { get; set; }

    public int Rows { get; set; } = 24;

    public int Columns { get; set; } = 80;

    public bool WantsMouse { get; set; }
}

public sealed class PtyPaneSnapshotDto
{
    public int Id { get; set; }

    public string Kind { get; set; } = "";

    public string Title { get; set; } = "";

    public string? SessionId { get; set; }

    public string? PtyId { get; set; }

    public string? Command { get; set; }

    public bool Focused { get; set; }

    public bool Exited { get; set; }

    public List<string>? Lines { get; set; }

    /** 发布时是否因总量上限被裁剪(只保留了尾行的一部分)。 */
    public bool Truncated { get; set; }
}

public sealed class PtyControlMessageDto
{
    public long Seq { get; set; }

    public string Kind { get; set; } = "";

    public int PaneId { get; set; }

    public string Payload { get; set; } = "";
}

public sealed class PtyDaemonSessionDto
{
    public string Id { get; set; } = "";

    public string Command { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; }

    public int? Pid { get; set; }

    public string Status { get; set; } = "";

    public int? ExitCode { get; set; }

    public bool IsAttached { get; set; }

    public int Columns { get; set; }

    public int Rows { get; set; }

    public bool WantsMouse { get; set; }

    /** 该 pty 里常驻 TUI 上报的 agent 会话 id(identify/publish-panes 写入); 无则 null。 */
    public string? AgentSessionId { get; set; }

    /** 该 pty 发布的窗格目录与尾行快照(publish-panes 写入); 无则 null。 */
    public List<PtyPaneSnapshotDto>? Panes { get; set; }

    /** 最近一次 publish-panes 的时间(daemon 记录); 无则 null。 */
    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed class PtyDaemonResponse
{
    public bool Ok { get; set; }

    public string? Error { get; set; }

    public List<PtyDaemonSessionDto>? Sessions { get; set; }

    public PtyDaemonSessionDto? Session { get; set; }

    /** method=control-read: 新控制消息; method=control-send: 分配到的 seq。 */
    public List<PtyControlMessageDto>? Controls { get; set; }

    public long Seq { get; set; }
}

internal static class PtyDaemonJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}