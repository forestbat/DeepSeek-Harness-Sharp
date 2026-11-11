using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin(Dsh.Agent.Plugin.Agent)]

namespace Dsh.Agent;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Agent = "@deepseek-ai/dsh-agent";

    public string[] Inject => packageName switch
    {
        // 会话内核先装配存储, 这里才能解析持久化后端与投影注册表。
        Agent => [SessionStore.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Agent => RegisterAgent(ctx, config),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    /** agent 循环与工具运行时: 顺序沿用拆分前的摆放, 只把内核的存储装配留在 dsh-core。 */
    private static IDisposable RegisterAgent(Context ctx, object? config)
    {
        _ = new SessionProjectionRegistry(ctx);
        _ = new SystemPrompt(ctx, SystemPromptConfigFrom(config));
        _ = new ToolRuntime(ctx);
        _ = new AgentRegistry(ctx);
        // 持久化插件声明依赖 core 的服务, 因此它总是晚于 core 加载; 这里必须按需解析而不是取值快照。
        _ = new AgentLoop(ctx, AgentLoopConfigFrom(config), _ => PersistenceOf(ctx));
        return new NoopDisposable();
    }

    private static ISessionPersistence PersistenceOf(Context ctx)
        => ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, false)
            ?? throw new InvalidOperationException("no session persistence backend configured for resume");

    private static SystemPromptConfig SystemPromptConfigFrom(object? config)
    {
        var dict = config as IReadOnlyDictionary<string, object?>;
        return new SystemPromptConfig
        {
            IncludeHarnessIdentity = BoolOf(dict, "includeHarnessIdentity") ?? new SystemPromptConfig().IncludeHarnessIdentity,
            IncludeRuntimeContext = BoolOf(dict, "includeRuntimeContext") ?? new SystemPromptConfig().IncludeRuntimeContext,
            IncludeEnvironment = BoolOf(dict, "includeEnvironment") ?? true,
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
