using Cordis;
using Dsh.Core;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Interaction.Plugin.Persona)]

namespace Dsh.Interaction;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Persona = "@deepseek-ai/dsh-persona";

    public string[] Inject => packageName switch
    {
        Persona => [SystemPrompt.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Persona => ApplyPersona(ctx, config),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

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