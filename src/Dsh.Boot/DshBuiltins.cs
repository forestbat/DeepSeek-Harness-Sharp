using Cordis;
using Dsh.Core;
using Dsh.Tools;

namespace Dsh.Boot;

// cordis.yml 里 `@deepseek-ai/...` 包名到本地已移植实现的映射;未命中的包名由 DshModuleImporter 回退给 NodeImporter。
public static class DshBuiltins
{
    public const string ToolBash = "@deepseek-ai/dsh-tool-bash";
    public const string ToolFs = "@deepseek-ai/dsh-tool-fs";
    public const string ToolFsSearch = "@deepseek-ai/dsh-tool-fs-search";
    public const string ToolTodo = "@deepseek-ai/dsh-tool-todo";
    public const string ToolStrReplaceEditor = "@deepseek-ai/dsh-tool-str-replace-editor";
    public const string FsLocal = "@deepseek-ai/dsh-fs-local";
    public const string Persona = "@deepseek-ai/dsh-persona";

    public static IReadOnlyDictionary<string, PluginDefinition> All { get; } = new Dictionary<string, PluginDefinition>
    {
        [ToolBash] = Define(ToolBash, [ToolRuntime.ServiceName, SystemPrompt.ServiceName, SubprocessService.ServiceName],
            (ctx, _) => BashTool.Register(ctx)),
        [ToolFs] = Define(ToolFs, [ToolRuntime.ServiceName, SystemPrompt.ServiceName],
            (ctx, _) => new DisposableBundle(ReadTool.Register(ctx), WriteTool.Register(ctx), EditTool.Register(ctx))),
        [ToolFsSearch] = Define(ToolFsSearch, [ToolRuntime.ServiceName, SystemPrompt.ServiceName],
            (ctx, _) => new DisposableBundle(GlobTool.Register(ctx), GrepTool.Register(ctx))),
        [ToolTodo] = Define(ToolTodo, [ToolRuntime.ServiceName],
            (ctx, config) => TodoWriteTool.Register(ctx, ConfigOf(config)?.GetValueOrDefault("allowParallelInProgress") as bool? ?? true)),
        [ToolStrReplaceEditor] = Define(ToolStrReplaceEditor, [ToolRuntime.ServiceName],
            (ctx, config) => StrReplaceEditorTool.Register(ctx, StrReplaceEditorConfigFrom(config))),
        [FsLocal] = Define(FsLocal, [],
            (ctx, config) => LocalFsService.Register(ctx, ConfigOf(config)?.GetValueOrDefault("cwd") as string)),
        [Persona] = Define(Persona, [SystemPrompt.ServiceName], ApplyPersona),
    };

    private static PluginDefinition Define(string name, string[] inject, Func<Context, object?, IDisposable> apply)
        => new()
        {
            Name = name,
            Inject = inject.ToDictionary<string, string, object?>(key => key, _ => null),
            Callback = new DelegatePluginCallback((ctx, config) =>
            {
                var registration = apply(ctx, config);
                return (Action)(() => registration.Dispose());
            }),
        };

    private static IReadOnlyDictionary<string, object?>? ConfigOf(object? config)
        => config as IReadOnlyDictionary<string, object?>;

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

    // TS persona 行的模板插值发生在渲染期(PromptRender),此处只注册字面 section。
    // includeRuntimeContext: false 对应 TS 的 suppressRuntimeContext。
    private static IDisposable ApplyPersona(Context ctx, object? config)
    {
        var systemPrompt = ctx.Get<SystemPrompt>(SystemPrompt.ServiceName)!;
        var dict = ConfigOf(config);
        var section = systemPrompt.ReplacePersona(
            dict?.GetValueOrDefault("text") as string ?? "",
            dict?.GetValueOrDefault("complete") is true);
        if (dict?.GetValueOrDefault("includeRuntimeContext") is not false)
            return section;
        return new DisposableBundle(section, systemPrompt.SuppressRuntimeContext());
    }

    private sealed class DisposableBundle(params IDisposable?[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable?.Dispose();
        }
    }
}
