using Dsh.Agent;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin("@deepseek-ai/dsh-ma2a")]

namespace Dsh.Ma2a.Tools;

public sealed class Plugin : IDshPlugin
{
    public string[] Inject => [ToolRuntime.ServiceName];

    public IDisposable Apply(Context ctx, object? config)
    {
        var tools = new Ma2aTools().Register(ctx);
        IDisposable? backendRegistration = null;
        if (ctx.Get<IAgentExecutionRouter>(IAgentExecutionRouter.ServiceName, false) is { } router)
        {
            var stateDir = (config as IReadOnlyDictionary<string, object?>)?.GetValueOrDefault("stateDirectory") as string
                ?? ctx.GetProp("ma2aStateDirectory") as string;
            backendRegistration = router.RegisterBackend(new Ma2aAgentExecutionBackend(stateDir));
        }
        return new CompositeDisposable(tools, backendRegistration);
    }

    private sealed class CompositeDisposable(IDisposable first, IDisposable? second) : IDisposable
    {
        public void Dispose()
        {
            second?.Dispose();
            first.Dispose();
        }
    }
}
