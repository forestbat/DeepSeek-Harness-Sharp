using Cordis;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Tools;

[assembly: DshPlugin(Dsh.Terminal.Plugin.Terminal)]
[assembly: DshPlugin(Dsh.Terminal.Plugin.TerminalBash)]
[assembly: DshPlugin(Dsh.Terminal.Plugin.ToolTerminal)]

namespace Dsh.Terminal;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Terminal = "@deepseek-ai/dsh-terminal";
    internal const string TerminalBash = "@deepseek-ai/dsh-terminal-bash";
    internal const string ToolTerminal = "@deepseek-ai/dsh-tool-terminal";

    public string[] Inject => packageName switch
    {
        Terminal => [TerminalSessionService.ServiceName],
        TerminalBash => [TerminalSessionService.ServiceName, SubprocessService.ServiceName],
        ToolTerminal => [TerminalSessionService.ServiceName, ToolRuntime.ServiceName, SystemPrompt.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Terminal => RegisterTerminalService(ctx),
        TerminalBash => Dsh.Terminal.TerminalBash.Register(ctx, TerminalBashConfigFrom(config)),
        ToolTerminal => TerminalTools.Register(ctx, TerminalToolsConfigFrom(config)),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable RegisterTerminalService(Context ctx)
    {
        _ = new TerminalSessionService(ctx);
        return new NoopDisposable();
    }

    private static IReadOnlyDictionary<string, object?>? ConfigOf(object? config)
        => config as IReadOnlyDictionary<string, object?>;

    private static TerminalBashConfig TerminalBashConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new TerminalBashConfig
        {
            BackendType = dict?.GetValueOrDefault("backendType") as string ?? TerminalBashConfig.DefaultBackendType,
            ShellDialect = ShellDialectOf(dict?.GetValueOrDefault("shellDialect") as string),
            ShellPath = dict?.GetValueOrDefault("shellPath") as string,
            ShellArgs = StringListOf(dict?.GetValueOrDefault("shellArgs")),
            Rows = IntOf(dict, "rows") ?? TerminalBashConfig.DefaultRows,
            Cols = IntOf(dict, "cols") ?? TerminalBashConfig.DefaultCols,
            ScrollbackLines = IntOf(dict, "scrollbackLines") ?? TerminalBashConfig.DefaultScrollbackLines,
            ScrollbackMaxBytes = IntOf(dict, "scrollbackMaxBytes") ?? TerminalBashConfig.DefaultScrollbackMaxBytes,
            MaxReadBytes = IntOf(dict, "maxReadBytes") ?? TerminalBashConfig.DefaultMaxReadBytes,
            PollIntervalMs = IntOf(dict, "pollIntervalMs") ?? TerminalBashConfig.DefaultPollIntervalMs,
            ExactProbeAfterMs = IntOf(dict, "exactProbeAfterMs") ?? TerminalBashConfig.DefaultExactProbeAfterMs,
            IdleSilenceMs = IntOf(dict, "idleSilenceMs") ?? TerminalBashConfig.DefaultIdleSilenceMs,
            HandoffGraceMs = IntOf(dict, "handoffGraceMs") ?? TerminalBashConfig.DefaultHandoffGraceMs,
            TimeoutMs = IntOf(dict, "timeoutMs") ?? TerminalBashConfig.DefaultTimeoutMs,
            DisposeGraceMs = IntOf(dict, "disposeGraceMs") ?? TerminalBashConfig.DefaultDisposeGraceMs,
        };
    }

    private static TerminalToolsConfig TerminalToolsConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new TerminalToolsConfig
        {
            EnableRunInBackground = dict?.GetValueOrDefault("enableRunInBackground") as bool? ?? new TerminalToolsConfig().EnableRunInBackground,
            MaxResultBytes = IntOf(dict, "maxResultBytes") ?? TerminalToolsConfig.DefaultMaxResultBytes,
        };
    }

    private static ShellDialect ShellDialectOf(string? value)
        => value == "pwsh" ? ShellDialect.Pwsh : ShellDialect.Bash;

    private static int? IntOf(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict?.GetValueOrDefault(key) switch
        {
            long value => (int)value,
            int value => value,
            _ => null,
        };

    private static IReadOnlyList<string>? StringListOf(object? value)
        => value is IEnumerable<object?> items
            ? items.OfType<string>().ToList()
            : null;

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}