using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Transport;

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
    /**
     * daemon run 目录的根: $DSH_HOME 或 ~/.dsh; 该根记录了 storage.root(§18)时用记录的根,
     * 否则 daemon 会把 socket 落在默认根、而数据在 storage.root 里, 两边互相找不到。
     * 与远端 host 的端点共用同一个解析实现(LocalEndpointRoot)。
     */
    public static string DefaultRoot() => LocalEndpointRoot.Resolve();

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

    /** Same-user proof read from the endpoint's token file (see LocalEndpointToken); every request must carry it. */
    public string? Token { get; set; }

    public string? Id { get; set; }

    public bool PlainText { get; set; }

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

    /** harness 持久化根(会话归属): 结构化传递, 不塞进命令行; daemon 据此向子进程注入 ChildHomeVariable。 */
    public string? Home { get; set; }

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

    /** 本会话的 harness 持久化根(启动时由 PtyDaemonStartParams.Home 指定); 用于按 home 归属筛选会话。 */
    public string? Home { get; set; }

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

    /** method=snapshot: 当前屏的状态重建重绘字节(VT 转义文本)。 */
    public string? Snapshot { get; set; }

    /** method=kill: 实际被终止的会话 id。 */
    public List<string>? Killed { get; set; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PtyDaemonRequest))]
[JsonSerializable(typeof(PtyDaemonResponse))]
internal partial class PtyDaemonJsonContext : JsonSerializerContext;
