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
}

public sealed class PtyDaemonResponse
{
    public bool Ok { get; set; }

    public string? Error { get; set; }

    public List<PtyDaemonSessionDto>? Sessions { get; set; }

    public PtyDaemonSessionDto? Session { get; set; }
}

internal static class PtyDaemonJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}