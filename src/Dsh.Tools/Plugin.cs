using Cordis;
using Dsh.Core;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Tools.Plugin.ToolBash)]
[assembly: DshPlugin(Dsh.Tools.Plugin.Subprocess)]
[assembly: DshPlugin(Dsh.Tools.Plugin.ToolPwsh)]
[assembly: DshPlugin(Dsh.Tools.Plugin.ToolFs)]
[assembly: DshPlugin(Dsh.Tools.Plugin.ToolFsSearch)]
[assembly: DshPlugin(Dsh.Tools.Plugin.ToolTodo)]
[assembly: DshPlugin(Dsh.Tools.Plugin.ToolStrReplaceEditor)]
[assembly: DshPlugin(Dsh.Tools.Plugin.FsLocal)]

namespace Dsh.Tools;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string ToolBash = "@deepseek-ai/dsh-tool-bash";
    internal const string Subprocess = "@deepseek-ai/dsh-subprocess";
    internal const string ToolPwsh = "@deepseek-ai/dsh-tool-pwsh";
    internal const string ToolFs = "@deepseek-ai/dsh-tool-fs";
    internal const string ToolFsSearch = "@deepseek-ai/dsh-tool-fs-search";
    internal const string ToolTodo = "@deepseek-ai/dsh-tool-todo";
    internal const string ToolStrReplaceEditor = "@deepseek-ai/dsh-tool-str-replace-editor";
    internal const string FsLocal = "@deepseek-ai/dsh-fs-local";

    public string[] Inject => packageName switch
    {
        Subprocess => [],
        ToolBash => [ToolRuntime.ServiceName, SystemPrompt.ServiceName, SubprocessService.ServiceName],
        ToolPwsh => [ToolRuntime.ServiceName, SystemPrompt.ServiceName, SubprocessService.ServiceName],
        ToolFs => [ToolRuntime.ServiceName, SystemPrompt.ServiceName],
        ToolFsSearch => [ToolRuntime.ServiceName, SystemPrompt.ServiceName],
        ToolTodo => [ToolRuntime.ServiceName],
        ToolStrReplaceEditor => [ToolRuntime.ServiceName],
        FsLocal => [],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Subprocess => RegisterSubprocess(ctx),
        ToolBash => BashTool.Register(ctx),
        ToolPwsh => PwshTool.Register(ctx),
        ToolFs => new CompositeDisposable(ReadTool.Register(ctx), WriteTool.Register(ctx), EditTool.Register(ctx)),
        ToolFsSearch => new CompositeDisposable(GlobTool.Register(ctx), GrepTool.Register(ctx)),
        ToolTodo => TodoWriteTool.Register(ctx, ConfigOf(config)?.GetValueOrDefault("allowParallelInProgress") as bool? ?? true),
        ToolStrReplaceEditor => StrReplaceEditorTool.Register(ctx, StrReplaceEditorConfigFrom(config)),
        FsLocal => LocalFsService.Register(ctx, ConfigOf(config)?.GetValueOrDefault("cwd") as string),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IReadOnlyDictionary<string, object?>? ConfigOf(object? config)
        => config as IReadOnlyDictionary<string, object?>;

    private static IDisposable RegisterSubprocess(Context ctx)
    {
        _ = new SubprocessService(ctx);
        return new NoopDisposable();
    }

    private static StrReplaceEditorConfig StrReplaceEditorConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new StrReplaceEditorConfig
        {
            MaxOutputChars = dict?.GetValueOrDefault("maxOutputChars") is long maxOutputChars
                ? (int)maxOutputChars
                : new StrReplaceEditorConfig().MaxOutputChars,
            Description = dict?.GetValueOrDefault("description") as string,
        };
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}