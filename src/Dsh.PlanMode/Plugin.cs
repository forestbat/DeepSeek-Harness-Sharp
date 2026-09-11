using Cordis;
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
        PlanMode => [ToolRuntime.ServiceName, SystemPrompt.ServiceName, SessionProjectionRegistry.ServiceName, UserQuestionService.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        PlanMode => RegisterPlanMode(ctx, config),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable RegisterPlanMode(Context ctx, object? config)
    {
        _ = new PlanModeController(ctx, PlanModeConfigFrom(config));
        return new NoopDisposable();
    }

    private static PlanModeConfig PlanModeConfigFrom(object? config)
        => new()
        {
            Section = (config as IReadOnlyDictionary<string, object?>)?.GetValueOrDefault("section") as string ?? "",
        };

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}