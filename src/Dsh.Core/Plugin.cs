using Cordis;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Core.Plugin.Core)]

namespace Dsh.Core;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Core = "@deepseek-ai/dsh-core";

    public string[] Inject => packageName switch
    {
        Core => [],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Core => RegisterCore(ctx, config),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable RegisterCore(Context ctx, object? config)
    {
        _ = new SessionStore(ctx);
        _ = new SystemPrompt(ctx, SystemPromptConfigFrom(config));
        _ = new ToolRuntime(ctx);
        _ = new LlmRuntime(ctx);
        _ = new AgentRegistry(ctx);
        var persistence = ctx.Get<ISessionPersistence>("sessionPersistence", false);
        _ = new AgentLoop(ctx, AgentLoopConfigFrom(config), persistence is null ? null : _ => persistence);
        return new NoopDisposable();
    }

    private static SystemPromptConfig SystemPromptConfigFrom(object? config)
    {
        var dict = config as IReadOnlyDictionary<string, object?>;
        return new SystemPromptConfig
        {
            IncludeHarnessIdentity = BoolOf(dict, "includeHarnessIdentity") ?? new SystemPromptConfig().IncludeHarnessIdentity,
            IncludeRuntimeContext = BoolOf(dict, "includeRuntimeContext") ?? new SystemPromptConfig().IncludeRuntimeContext,
            Persona = dict?.GetValueOrDefault("persona") as string ?? "",
        };
    }

    private static AgentLoopConfig AgentLoopConfigFrom(object? config)
    {
        var dict = config as IReadOnlyDictionary<string, object?>;
        return new AgentLoopConfig
        {
            MaxParallelToolCalls = IntOf(dict, "maxParallelToolCalls") ?? AgentLoopConfig.DefaultMaxParallelToolCalls,
        };
    }

    private static bool? BoolOf(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict?.GetValueOrDefault(key) as bool?;

    private static int? IntOf(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict?.GetValueOrDefault(key) switch
        {
            long value => (int)value,
            int value => value,
            _ => null,
        };

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
