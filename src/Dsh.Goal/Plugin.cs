using Cordis;
using Dsh.Core;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Goal.Plugin.Goal)]
[assembly: DshPlugin(Dsh.Goal.Plugin.ToolGoal)]

namespace Dsh.Goal;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Goal = "@deepseek-ai/dsh-goal";
    internal const string ToolGoal = "@deepseek-ai/dsh-tool-goal";

    public string[] Inject => packageName switch
    {
        Goal => [AgentRegistry.ServiceName, SessionProjectionRegistry.ServiceName],
        ToolGoal => [ToolRuntime.ServiceName, SystemPrompt.ServiceName, AgentRegistry.ServiceName, SessionProjectionRegistry.ServiceName, GoalService.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Goal => RegisterGoalService(ctx, config),
        ToolGoal => GoalTools.Apply(ctx, GoalToolsConfigFrom(config)),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable RegisterGoalService(Context ctx, object? config)
    {
        _ = new GoalService(ctx, GoalServiceConfigFrom(config));
        return new NoopDisposable();
    }

    private static IReadOnlyDictionary<string, object?>? ConfigOf(object? config)
        => config as IReadOnlyDictionary<string, object?>;

    private static GoalServiceConfig GoalServiceConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new GoalServiceConfig
        {
            DefaultMaxGoalRounds = LongOf(dict, "defaultMaxGoalRounds") ?? new GoalServiceConfig().DefaultMaxGoalRounds,
        };
    }

    private static GoalToolsConfig GoalToolsConfigFrom(object? config)
    {
        var dict = ConfigOf(config);
        return new GoalToolsConfig
        {
            BlockedAfterConsecutiveRounds = LongOf(dict, "blockedAfterConsecutiveRounds") ?? GoalToolsConfig.DefaultBlockedAfterConsecutiveRounds,
        };
    }

    private static long? LongOf(IReadOnlyDictionary<string, object?>? dict, string key)
        => dict?.GetValueOrDefault(key) switch
        {
            long value => value,
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