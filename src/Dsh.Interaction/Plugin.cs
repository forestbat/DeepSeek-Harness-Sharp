using Cordis;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Interaction.Plugin.Interaction)]
[assembly: DshPlugin(Dsh.Interaction.Plugin.Persona)]

namespace Dsh.Interaction;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Interaction = "@deepseek-ai/dsh-interaction";
    internal const string Persona = "@deepseek-ai/dsh-persona";

    public string[] Inject => packageName switch
    {
        Interaction => [SystemPrompt.ServiceName, ToolRuntime.ServiceName, LlmRuntime.ServiceName],
        Persona => [SystemPrompt.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Interaction => ApplyInteraction(ctx),
        Persona => ApplyPersona(ctx, config),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable ApplyInteraction(Context ctx)
    {
        _ = ApprovalService.Register(ctx);
        _ = UserQuestionService.Register(ctx);
        _ = CommandsService.Register(ctx);

        var options = ctx.GetProp("harnessOptions") as HarnessOptions
            ?? throw new InvalidOperationException("harnessOptions is required for the interaction plugin");
        var catalog = ctx.GetProp("pluginCatalog") as PluginCatalog
            ?? throw new InvalidOperationException("pluginCatalog is required for the interaction plugin");
        var settings = HarnessSettings.Load(options.Home);
        return new DisposableBundle(
            ModelCommand.Register(ctx, options.Home),
            ReasoningCommand.Register(ctx),
            ProviderCommand.Register(ctx, options.Home),
            MemoryCommand.Register(ctx, options),
            PluginCommand.Register(ctx, catalog),
            SafetyCommandGuard.Register(ctx, settings.Safety));
    }

    private static IReadOnlyDictionary<string, object?>? ConfigOf(object? config)
        => config as IReadOnlyDictionary<string, object?>;

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