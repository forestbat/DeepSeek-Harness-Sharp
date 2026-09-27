using Dsh.Runtime;
using Dsh.Core;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.PtyTerminal.Plugin.Terminal)]
[assembly: DshPlugin(Dsh.PtyTerminal.Plugin.ToolTerminal)]

namespace Dsh.PtyTerminal;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Terminal = "@deepseek-ai/dsh-terminal";
    internal const string ToolTerminal = "@deepseek-ai/dsh-tool-terminal";

    public string[] Inject => packageName switch
    {
        Terminal => [],
        ToolTerminal => [TerminalSessionService.ServiceName, ToolRuntime.ServiceName, SystemPrompt.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Terminal => RegisterTerminalService(ctx, config),
        ToolTerminal => TerminalTools.Register(ctx, TerminalToolsConfigFrom(config)),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IReadOnlyDictionary<string, object?>? ConfigOf(object? config)
        => config as IReadOnlyDictionary<string, object?>;

    private static IDisposable RegisterTerminalService(Context ctx, object? config)
    {
        var terminals = new TerminalSessionService(ctx);
        return PtyTerminalPlugin.RegisterBackend(terminals, PtyTerminalConfigFrom(config));
    }

    private static PtyTerminalConfig PtyTerminalConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new PtyTerminalConfig
        {
            Command = dict?.GetValueOrDefault("command") as string,
            Args = StringListOf(dict?.GetValueOrDefault("args")),
            Rows = IntOf(dict, "rows") ?? PtyTerminalConfig.DefaultRows,
            Cols = IntOf(dict, "cols") ?? PtyTerminalConfig.DefaultCols,
            ScrollbackLines = IntOf(dict, "scrollbackLines") ?? PtyTerminalConfig.DefaultScrollbackLines,
            ScrollbackMaxBytes = IntOf(dict, "scrollbackMaxBytes") ?? PtyTerminalConfig.DefaultScrollbackMaxBytes,
            MaxReadBytes = IntOf(dict, "maxReadBytes") ?? PtyTerminalConfig.DefaultMaxReadBytes,
            PollIntervalMs = IntOf(dict, "pollIntervalMs") ?? PtyTerminalConfig.DefaultPollIntervalMs,
            IdleSilenceMs = IntOf(dict, "idleSilenceMs") ?? PtyTerminalConfig.DefaultIdleSilenceMs,
            HandoffGraceMs = IntOf(dict, "handoffGraceMs") ?? PtyTerminalConfig.DefaultHandoffGraceMs,
            TimeoutMs = IntOf(dict, "timeoutMs") ?? PtyTerminalConfig.DefaultTimeoutMs,
            DisposeGraceMs = IntOf(dict, "disposeGraceMs") ?? PtyTerminalConfig.DefaultDisposeGraceMs,
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
}
