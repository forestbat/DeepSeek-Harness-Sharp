using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Agent;

[assembly: DshPlugin(Dsh.Schedule.Plugin.Schedule)]
[assembly: DshPlugin(Dsh.Schedule.Plugin.ToolSchedule)]

namespace Dsh.Schedule;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Schedule = "@deepseek-ai/dsh-schedule";
    internal const string ToolSchedule = "@deepseek-ai/dsh-tool-schedule";

    public string[] Inject => packageName switch
    {
        Schedule => [AgentRegistry.ServiceName],
        ToolSchedule => [ToolRuntime.ServiceName, SystemPrompt.ServiceName, ScheduleService.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Schedule => new ScheduleService(ctx),
        ToolSchedule => ScheduleTools.Apply(ctx),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };
}
