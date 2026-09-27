namespace Dsh.PtyTerminal;

/**
 * 真 PTY 终端后端配置: 键名与旧 shell 后端一致, 默认沿用 rows=24/cols=80 与相同的时序/留存上限。
 * command/args 留空时按平台解析 shell(Windows 取 pwsh/powershell/cmd, Unix 取 $SHELL 或 /bin/bash)。
 */
public sealed record PtyTerminalConfig
{
    public const string DefaultBackendType = "shell";
    public const string DefaultUnixShell = "/bin/bash";
    public const string DefaultWindowsShell = "pwsh";
    public const int DefaultRows = 24;
    public const int DefaultCols = 80;
    public const int DefaultScrollbackLines = 10_000;
    public const int DefaultScrollbackMaxBytes = 4 * 1024 * 1024;
    public const int DefaultMaxReadBytes = 256 * 1024;
    public const int DefaultPollIntervalMs = 50;
    public const int DefaultIdleSilenceMs = 3_000;
    public const int DefaultHandoffGraceMs = 500;
    public const int DefaultTimeoutMs = 30_000;
    public const int DefaultDisposeGraceMs = 3_000;

    public string? Command { get; init; }
    public IReadOnlyList<string>? Args { get; init; }
    public int Rows { get; init; } = DefaultRows;
    public int Cols { get; init; } = DefaultCols;
    public int ScrollbackLines { get; init; } = DefaultScrollbackLines;
    public int ScrollbackMaxBytes { get; init; } = DefaultScrollbackMaxBytes;
    public int MaxReadBytes { get; init; } = DefaultMaxReadBytes;
    public int PollIntervalMs { get; init; } = DefaultPollIntervalMs;
    public int IdleSilenceMs { get; init; } = DefaultIdleSilenceMs;
    public int HandoffGraceMs { get; init; } = DefaultHandoffGraceMs;
    public int TimeoutMs { get; init; } = DefaultTimeoutMs;
    public int DisposeGraceMs { get; init; } = DefaultDisposeGraceMs;
}

public sealed record ResolvedPtyTerminalConfig
{
    public required string Command { get; init; }
    public required IReadOnlyList<string> Args { get; init; }
    public required int Rows { get; init; }
    public required int Cols { get; init; }
    public required int ScrollbackLines { get; init; }
    public required int ScrollbackMaxBytes { get; init; }
    public required int MaxReadBytes { get; init; }
    public required int PollIntervalMs { get; init; }
    public required int IdleSilenceMs { get; init; }
    public required int HandoffGraceMs { get; init; }
    public required int TimeoutMs { get; init; }
    public required int DisposeGraceMs { get; init; }
}

public static class PtyTerminalConfigResolver
{
    private static readonly string[] WindowsShellCandidates = ["pwsh", "powershell", "cmd"];
    private static readonly string[] WindowsExecutableExtensions = [".exe", ".cmd", ".bat", ""];
    private static readonly IReadOnlyList<string> DefaultBashArgs = ["--noprofile", "--norc", "-i"];
    private static readonly IReadOnlyList<string> DefaultPwshArgs = ["-NoLogo", "-NoProfile"];
    private static readonly IReadOnlyList<string> DefaultInteractiveArgs = ["-i"];

    public static ResolvedPtyTerminalConfig Resolve(PtyTerminalConfig? config)
    {
        var source = config ?? new PtyTerminalConfig();
        var command = !string.IsNullOrEmpty(source.Command) ? source.Command : DefaultShell();
        var args = source.Args is { Count: > 0 } ? source.Args : DefaultArgs(command);
        return new ResolvedPtyTerminalConfig
        {
            Command = command,
            Args = args,
            Rows = source.Rows,
            Cols = source.Cols,
            ScrollbackLines = source.ScrollbackLines,
            ScrollbackMaxBytes = source.ScrollbackMaxBytes,
            MaxReadBytes = source.MaxReadBytes,
            PollIntervalMs = source.PollIntervalMs,
            IdleSilenceMs = source.IdleSilenceMs,
            HandoffGraceMs = source.HandoffGraceMs,
            TimeoutMs = source.TimeoutMs,
            DisposeGraceMs = source.DisposeGraceMs,
        };
    }

    public static void Validate(ResolvedPtyTerminalConfig config)
    {
        if (config.Command.Length == 0)
            throw new InvalidOperationException("pty-terminal: command must be non-empty");
        if (config.Rows <= 0)
            throw new InvalidOperationException("pty-terminal: rows must be a positive safe integer");
        if (config.Cols <= 0)
            throw new InvalidOperationException("pty-terminal: cols must be a positive safe integer");
        if (config.ScrollbackLines <= 0)
            throw new InvalidOperationException("pty-terminal: scrollbackLines must be a positive safe integer");
        if (config.ScrollbackMaxBytes <= 0)
            throw new InvalidOperationException("pty-terminal: scrollbackMaxBytes must be a positive safe integer");
        if (config.MaxReadBytes <= 0)
            throw new InvalidOperationException("pty-terminal: maxReadBytes must be a positive safe integer");
        if (config.PollIntervalMs <= 0)
            throw new InvalidOperationException("pty-terminal: pollIntervalMs must be a positive safe integer");
        if (config.IdleSilenceMs <= 0)
            throw new InvalidOperationException("pty-terminal: idleSilenceMs must be a positive safe integer");
        if (config.HandoffGraceMs <= 0)
            throw new InvalidOperationException("pty-terminal: handoffGraceMs must be a positive safe integer");
        if (config.TimeoutMs <= 0)
            throw new InvalidOperationException("pty-terminal: timeoutMs must be a positive safe integer");
        if (config.DisposeGraceMs <= 0)
            throw new InvalidOperationException("pty-terminal: disposeGraceMs must be a positive safe integer");
        if (config.MaxReadBytes > config.ScrollbackMaxBytes)
            throw new InvalidOperationException("pty-terminal: maxReadBytes must not exceed scrollbackMaxBytes");
        if (config.HandoffGraceMs < config.PollIntervalMs)
            throw new InvalidOperationException("pty-terminal: handoffGraceMs must be at least pollIntervalMs so one readiness poll runs inside the grace window");
    }

    private static string DefaultShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            var shell = Environment.GetEnvironmentVariable("SHELL");
            return string.IsNullOrEmpty(shell) ? PtyTerminalConfig.DefaultUnixShell : shell;
        }
        foreach (var candidate in WindowsShellCandidates)
        {
            if (ResolveOnPath(candidate) is { } resolved)
                return resolved;
        }
        return $"{PtyTerminalConfig.DefaultWindowsShell}.exe";
    }

    private static IReadOnlyList<string> DefaultArgs(string command)
    {
        var name = Path.GetFileNameWithoutExtension(command).ToLowerInvariant();
        return name switch
        {
            "pwsh" or "powershell" => DefaultPwshArgs,
            "cmd" => [],
            "bash" => DefaultBashArgs,
            _ => DefaultInteractiveArgs,
        };
    }

    private static string? ResolveOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;
            foreach (var extension in WindowsExecutableExtensions)
            {
                var candidate = Path.Combine(directory, executable + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }
}
