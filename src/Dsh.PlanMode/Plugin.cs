using Dsh.Runtime;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.PlanMode.Plugin.PlanMode)]

namespace Dsh.PlanMode;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string PlanMode = "@deepseek-ai/dsh-plan-mode";

    public string[] Inject => packageName switch
    {
        PlanMode => [ToolRuntime.ServiceName, SystemPrompt.ServiceName, SessionProjectionRegistry.ServiceName, UserQuestionService.ServiceName, CommandsService.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        PlanMode => RegisterPlanMode(ctx, config),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable RegisterPlanMode(Context ctx, object? config)
        => new PlanModeController(ctx, PlanModeConfigFrom(config));

    private static PlanModeConfig PlanModeConfigFrom(object? config)
    {
        var section = (config as IReadOnlyDictionary<string, object?>)?.GetValueOrDefault("section") as string;
        return new PlanModeConfig
        {
            Section = string.IsNullOrWhiteSpace(section) ? PlanModeConfig.DefaultSection : section,
        };
    }
}
