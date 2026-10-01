namespace Dsh.Pty;

public readonly record struct PtySessionId(string Value)
{
    public static PtySessionId Create(string value) => new(value);

    public override string ToString() => Value;
}

public enum PtySessionStatus
{
    Running,
    Exited,
}

public sealed record PtyStartInfo
{
    public required string FileName { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    public int Rows { get; init; } = 24;

        public int Columns { get; init; } = 80;

        /** 子进程要鼠标事件(常驻 TUI); 终端形态的代理据此才打开宿主终端的上报, 见 PtyDaemonClient.AttachAsync。 */
        public bool WantsMouse { get; init; }

        public string Command => string.Join(' ', new[] { FileName }.Concat(Arguments));
}

public sealed record PtySessionInfo(
    PtySessionId Id,
    string Command,
    DateTimeOffset StartedAt,
    int? Pid,
    PtySessionStatus Status,
    int? ExitCode,
    bool IsAttached,
    int Columns = 0,
    int Rows = 0,
    bool WantsMouse = false);
