namespace Dsh.Terminal;

public enum ShellDialect
{
    Bash,
    Pwsh,
}

public sealed record TerminalBashConfig
{
    public const string DefaultBackendType = "shell";
    public const string DefaultBashShell = "/bin/bash";
    public const string DefaultPwshShell = "pwsh";
    public const int DefaultRows = 40;
    public const int DefaultCols = 160;
    public const int DefaultScrollbackLines = 10_000;
    public const int DefaultScrollbackMaxBytes = 4 * 1024 * 1024;
    public const int DefaultMaxReadBytes = 256 * 1024;
    public const int DefaultPollIntervalMs = 50;
    public const int DefaultExactProbeAfterMs = 150;
    public const int DefaultIdleSilenceMs = 3_000;
    public const int DefaultHandoffGraceMs = 500;
    public const int DefaultTimeoutMs = 30_000;
    public const int DefaultDisposeGraceMs = 3_000;

    public string BackendType { get; init; } = DefaultBackendType;
    public ShellDialect ShellDialect { get; init; } = ShellDialect.Bash;
    public string? ShellPath { get; init; }
    public IReadOnlyList<string>? ShellArgs { get; init; }
    public int Rows { get; init; } = DefaultRows;
    public int Cols { get; init; } = DefaultCols;
    public int ScrollbackLines { get; init; } = DefaultScrollbackLines;
    public int ScrollbackMaxBytes { get; init; } = DefaultScrollbackMaxBytes;
    public int MaxReadBytes { get; init; } = DefaultMaxReadBytes;
    public int PollIntervalMs { get; init; } = DefaultPollIntervalMs;
    public int ExactProbeAfterMs { get; init; } = DefaultExactProbeAfterMs;
    public int IdleSilenceMs { get; init; } = DefaultIdleSilenceMs;
    public int HandoffGraceMs { get; init; } = DefaultHandoffGraceMs;
    public int TimeoutMs { get; init; } = DefaultTimeoutMs;
    public int DisposeGraceMs { get; init; } = DefaultDisposeGraceMs;
}

public sealed record ResolvedTerminalBashConfig
{
    public required string BackendType { get; init; }
    public required ShellDialect ShellDialect { get; init; }
    public required string ShellPath { get; init; }
    public required IReadOnlyList<string> ShellArgs { get; init; }
    public required int Rows { get; init; }
    public required int Cols { get; init; }
    public required int ScrollbackLines { get; init; }
    public required int ScrollbackMaxBytes { get; init; }
    public required int MaxReadBytes { get; init; }
    public required int PollIntervalMs { get; init; }
    public required int ExactProbeAfterMs { get; init; }
    public required int IdleSilenceMs { get; init; }
    public required int HandoffGraceMs { get; init; }
    public required int TimeoutMs { get; init; }
    public required int DisposeGraceMs { get; init; }
}

public static class TerminalBashConfigResolver
{
    private static readonly IReadOnlyList<string> DefaultBashArgs = ["--noprofile", "--norc", "-i"];
    private static readonly IReadOnlyList<string> DefaultPwshArgs = ["-NoLogo", "-NoProfile"];

    public static ResolvedTerminalBashConfig Resolve(TerminalBashConfig? config)
    {
        var source = config ?? new TerminalBashConfig();
        var dialect = source.ShellDialect;
        var shellPath = !string.IsNullOrEmpty(source.ShellPath)
            ? source.ShellPath
            : dialect == ShellDialect.Pwsh
                ? TerminalBashConfig.DefaultPwshShell
                : OperatingSystem.IsWindows() ? "bash" : TerminalBashConfig.DefaultBashShell;
        var shellArgs = source.ShellArgs is { Count: > 0 }
            ? source.ShellArgs
            : dialect == ShellDialect.Pwsh ? DefaultPwshArgs : DefaultBashArgs;
        return new ResolvedTerminalBashConfig
        {
            BackendType = source.BackendType,
            ShellDialect = dialect,
            ShellPath = shellPath,
            ShellArgs = shellArgs,
            Rows = source.Rows,
            Cols = source.Cols,
            ScrollbackLines = source.ScrollbackLines,
            ScrollbackMaxBytes = source.ScrollbackMaxBytes,
            MaxReadBytes = source.MaxReadBytes,
            PollIntervalMs = source.PollIntervalMs,
            ExactProbeAfterMs = source.ExactProbeAfterMs,
            IdleSilenceMs = source.IdleSilenceMs,
            HandoffGraceMs = source.HandoffGraceMs,
            TimeoutMs = source.TimeoutMs,
            DisposeGraceMs = source.DisposeGraceMs,
        };
    }

    public static void Validate(ResolvedTerminalBashConfig config)
    {
        if (config.BackendType.Length == 0)
            throw new InvalidOperationException("terminal-bash: backendType must be non-empty");
        if (config.ShellPath.Length == 0)
            throw new InvalidOperationException("terminal-bash: shellPath must be non-empty");
        if (config.Rows <= 0)
            throw new InvalidOperationException("terminal-bash: rows must be a positive safe integer");
        if (config.Cols <= 0)
            throw new InvalidOperationException("terminal-bash: cols must be a positive safe integer");
        if (config.ScrollbackLines <= 0)
            throw new InvalidOperationException("terminal-bash: scrollbackLines must be a positive safe integer");
        if (config.ScrollbackMaxBytes <= 0)
            throw new InvalidOperationException("terminal-bash: scrollbackMaxBytes must be a positive safe integer");
        if (config.MaxReadBytes <= 0)
            throw new InvalidOperationException("terminal-bash: maxReadBytes must be a positive safe integer");
        if (config.PollIntervalMs <= 0)
            throw new InvalidOperationException("terminal-bash: pollIntervalMs must be a positive safe integer");
        if (config.ExactProbeAfterMs <= 0)
            throw new InvalidOperationException("terminal-bash: exactProbeAfterMs must be a positive safe integer");
        if (config.IdleSilenceMs <= 0)
            throw new InvalidOperationException("terminal-bash: idleSilenceMs must be a positive safe integer");
        if (config.HandoffGraceMs <= 0)
            throw new InvalidOperationException("terminal-bash: handoffGraceMs must be a positive safe integer");
        if (config.TimeoutMs <= 0)
            throw new InvalidOperationException("terminal-bash: timeoutMs must be a positive safe integer");
        if (config.DisposeGraceMs <= 0)
            throw new InvalidOperationException("terminal-bash: disposeGraceMs must be a positive safe integer");
        if (config.MaxReadBytes > config.ScrollbackMaxBytes)
            throw new InvalidOperationException("terminal-bash: maxReadBytes must not exceed scrollbackMaxBytes");
        if (config.HandoffGraceMs < config.PollIntervalMs)
            throw new InvalidOperationException("terminal-bash: handoffGraceMs must be at least pollIntervalMs so one readiness poll runs inside the grace window");
    }
}
